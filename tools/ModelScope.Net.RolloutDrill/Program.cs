using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelScope.Net;
using ModelScope.Net.Runtime;
using ModelScope.Net.Runtime.Onnx;

if (args.Length == 2 && args[0] == "--restore-probe")
{
    await RestoreProbe(args[1]);
    return;
}
if (args.Length == 2 && args[0] == "--restore-store-probe")
{
    await RestoreStoreProbe(args[1]);
    return;
}
var holdAfterCheckpoint = args.Length == 4 && args[3] == "--hold-after-checkpoint";
var holdAfterStoreStage = args.Length == 4 && args[3] == "--hold-after-store-stage";
if (args.Length != 3 && !holdAfterCheckpoint && !holdAfterStoreStage)
    throw new ArgumentException("Usage: ModelScope.Net.RolloutDrill MODEL_DIRECTORY PYTHON_GOLD_JSON OUTPUT_DIRECTORY");
var modelPath = Path.GetFullPath(args[0]);
var goldPath = Path.GetFullPath(args[1]);
var outputPath = Path.GetFullPath(args[2]);
Directory.CreateDirectory(outputPath);
using var goldDocument = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath));
var gold = goldDocument.RootElement;
var modelId = gold.GetProperty("modelId").GetString()!;
var revision = gold.GetProperty("revision").GetString()!;
var expected = gold.GetProperty("embeddings").EnumerateArray()
    .Select(row => row.EnumerateArray().Select(value => value.GetDouble()).ToArray()).ToArray();
var texts = gold.GetProperty("texts").EnumerateArray().Select(value => value.GetString()).ToArray();
if (expected.Length != texts.Length || expected.Length == 0) throw new InvalidDataException("Invalid gold samples.");
var pooling = Enum.Parse<OnnxEmbeddingPooling>(gold.GetProperty("pooling").GetString()!, ignoreCase: true);
var maxLength = gold.GetProperty("maxLength").GetInt32();
var normalize = gold.GetProperty("normalize").GetBoolean();
using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modelPath, ".modelscope-net-manifest.json")));
if (manifest.RootElement.GetProperty("resolvedRevision").GetString() != revision)
    throw new InvalidDataException("Gold and snapshot revisions differ.");
var hashes = manifest.RootElement.GetProperty("files").EnumerateArray().ToDictionary(
    file => file.GetProperty("path").GetString()!, file => file.GetProperty("sha256").GetString()!);
var capabilities = new ModelCapabilities(modelPath, modelId, revision, "sentence-embedding", ["BertModel"],
    [new("onnx/model.onnx", ModelArtifactFormat.Onnx, new FileInfo(Path.Combine(modelPath, "onnx/model.onnx")).Length)],
    [], false, []);
var platform = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}/{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
var runtimeAssets = new SortedDictionary<string, string>(StringComparer.Ordinal);
foreach (var assembly in new[] { typeof(OnnxEmbeddingRuntime).Assembly, typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly })
    runtimeAssets[Path.GetFileName(assembly.Location)] = await HashFile(assembly.Location);
foreach (var native in Directory.EnumerateFiles(AppContext.BaseDirectory, "*onnxruntime*", SearchOption.AllDirectories)
    .Where(path => path.EndsWith(".dylib", StringComparison.Ordinal) || path.EndsWith(".so", StringComparison.Ordinal) ||
        path.EndsWith(".dll", StringComparison.Ordinal)))
    runtimeAssets[Path.GetRelativePath(AppContext.BaseDirectory, native)] = await HashFile(native);
using var rsa = RSA.Create(2048);
await File.WriteAllTextAsync(Path.Combine(outputPath, "drill-public-key.pem"), rsa.ExportSubjectPublicKeyInfoPem());
var request = new ModelRequest("sentence-embedding", JsonSerializer.SerializeToElement(new { texts }));
var deployments = new Dictionary<string, (ProductionRuntimePolicy Policy, IModelRuntime Runtime, string Digest,
    string CatalogPath, string SignaturePath)>();
var stable = await CreateRelease("stable", normalize);
var candidate = await CreateRelease("candidate", normalize);
var rollout = new ModelReleaseRollout(stable, new(1, TimeSpan.Zero, TimeSpan.FromMinutes(2)));
var steps = new List<object>();
await rollout.StageAsync(candidate, request, IsGold);
var key = Enumerable.Range(0, 10000).Select(i => $"drill-{i}").First(k =>
    BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(k))) % 100 < 5);
foreach (var percent in new[] { 5, 25, 50, 100 })
{
    var result = await rollout.InvokeAsync(key, request);
    if (!IsGold(result) || rollout.GetSnapshot().CandidatePercent != percent)
        throw new InvalidDataException("Canary numerical or routing check failed.");
    steps.Add(new { stage = percent, goldPassed = true, state = rollout.GetSnapshot() });
    rollout.Advance();
}
if (rollout.GetSnapshot().StableRelease != candidate.Id) throw new InvalidDataException("Promotion failed.");
rollout.Rollback();
var restored = await rollout.InvokeAsync(key, request);
if (rollout.GetSnapshot().StableRelease != stable.Id || !IsGold(restored))
    throw new InvalidDataException("Rollback did not restore the gold result.");
var badCandidate = await CreateRelease("bad-normalization", !normalize);
var rejected = false;
try { await rollout.StageAsync(badCandidate, request, IsGold); }
catch (ModelScopeException e) when (e.Code == ModelScopeErrorCode.InferenceFailed) { rejected = true; }
if (!rejected || rollout.GetSnapshot().CandidateRelease is not null)
    throw new InvalidDataException("Numerical regression did not block promotion.");
await rollout.StageAsync(await CreateRelease("revoked-candidate", normalize), request, IsGold);
rollout.Revoke("revoked-candidate");
if (rollout.GetSnapshot().CandidateRelease is not null || !IsGold(await rollout.InvokeAsync(key, request)))
    throw new InvalidDataException("Revocation did not restore stable routing.");
var signedRevocation = await ExerciseSignedRevocation();
var durablePublication = await ExerciseDurablePublication();
var report = new
{
    schemaVersion = 1,
    status = "passed",
    scope = "local-real-model-rollout-drill",
    completedAt = DateTimeOffset.UtcNow,
    modelId,
    revision,
    platform,
    goldSha256 = await HashFile(goldPath),
    runtimeAssets,
    samplesPerRequest = texts.Length,
    dimensions = expected[0].Length,
    maximumAbsoluteErrorThreshold = 1e-4,
    stages = steps,
    promotionPassed = true,
    rollbackGoldPassed = true,
    numericalRegressionBlocked = rejected,
    revocationPassed = true,
    signedRevocation,
    durablePublication,
    finalState = rollout.GetSnapshot(),
    limitations = new[]
    {
        "Same fixed model and runtime build; blue/green release identities differ. No new model revision certification is claimed.",
        "Ephemeral drill signing key is not a production trust root. Catalogs and public key are retained; private key is not persisted.",
        "Accelerated stage gates (one request, no observation delay) exercise transitions; not production capacity or soak evidence.",
        "Target cluster deployment, cross-platform runtime execution and distributed routing remain separate acceptance gates."
    }
};
await File.WriteAllTextAsync(Path.Combine(outputPath, "report.json"),
    JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
Console.WriteLine($"Rollout drill passed: {texts.Length} gold samples, 4 stages, rollback, numerical rejection and revocation.");

bool IsGold(ModelResponse response)
{
    var rows = response.Output.GetProperty("embeddings").EnumerateArray().ToArray();
    if (rows.Length != expected.Length) return false;
    for (var i = 0; i < rows.Length; i++)
    {
        var values = rows[i].EnumerateArray().Select(v => v.GetDouble()).ToArray();
        if (values.Length != expected[i].Length) return false;
        for (var j = 0; j < values.Length; j++)
            if (!double.IsFinite(values[j]) || Math.Abs(values[j] - expected[i][j]) > 1e-4) return false;
    }
    return true;
}

async Task<VerifiedModelRelease> CreateRelease(string id, bool normalization)
{
    var runtimeBundle = JsonSerializer.SerializeToUtf8Bytes(new
    {
        runtimeAssets,
        maxLength,
        pooling = pooling.ToString(),
        normalize = normalization
    });
    var runtimeDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(runtimeBundle));
    var catalog = new CompatibilityCatalog(1, "drill-" + id, DateTimeOffset.UtcNow,
        [new(modelId, revision, "sentence-embedding", "onnx-embedding", CapabilityStatus.Certified,
            hashes, [platform], runtimeDigest)]);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var catalogPath = Path.Combine(outputPath, id + ".catalog.json");
    var signaturePath = Path.Combine(outputPath, id + ".catalog.sig");
    await File.WriteAllBytesAsync(catalogPath, bytes);
    await File.WriteAllTextAsync(signaturePath,
        Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    var options = new OnnxRuntimeOptions { ModelFile = "onnx/model.onnx" };
    options.CertifiedModels.Add(modelId);
    var runtime = new OnnxEmbeddingRuntime(new OnnxRuntimeAdapter(options),
        new OnnxEmbeddingOptions { MaxLength = maxLength, Normalize = normalization, Pooling = pooling });
    var policy = await ProductionRuntimePolicy.LoadAsync(catalogPath, signaturePath,
        rsa.ExportSubjectPublicKeyInfoPem(), platform,
        new Dictionary<string, string> { [runtime.Name] = runtimeDigest });
    var now = DateTimeOffset.UtcNow;
    var revocations = JsonSerializer.SerializeToUtf8Bytes(
        new CertificationRevocationList(1, catalog.CatalogVersion, 1, now, now.AddHours(1), []),
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var revocationPath = Path.Combine(outputPath, id + ".revocations.json");
    var revocationSignature = Path.Combine(outputPath, id + ".revocations.sig");
    await File.WriteAllBytesAsync(revocationPath, revocations);
    await File.WriteAllTextAsync(revocationSignature,
        Convert.ToBase64String(rsa.SignData(revocations, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    await policy.ApplyRevocationsAsync(revocationPath, revocationSignature);
    deployments.Add(id, (policy, runtime, runtimeDigest, catalogPath, signaturePath));
    return await VerifiedModelRelease.CreateAsync(id, catalogPath, signaturePath, rsa.ExportSubjectPublicKeyInfoPem(),
        capabilities, runtime.Name, runtimeDigest, platform,
        new RuntimeRouter([runtime], new RuntimeRouterOptions
        {
            Mode = RuntimePolicyMode.Production,
            ProductionPolicy = policy,
        }));
}

async Task<object> ExerciseSignedRevocation()
{
    // Separate policy identity: the release-object rollback checks above are not evidence for signed policy revocation.
    await CreateRelease("signed-policy", normalize);
    var deployed = deployments["signed-policy"];
    RuntimeRouter Router(ProductionRuntimePolicy policy) => new([deployed.Runtime], new()
    {
        Mode = RuntimePolicyMode.Production, ProductionPolicy = policy, AllowExperimentalInProduction = true
    });
    var router = Router(deployed.Policy);
    await using var session = await router.CreateSessionAsync(capabilities);
    if (!IsGold(await session.InvokeAsync(request))) throw new InvalidDataException("Pre-revocation gold failed.");
    var oldCheckpoint = Path.Combine(outputPath, "signed-policy.before.checkpoint.json");
    await deployed.Policy.SaveRevocationCheckpointAsync(oldCheckpoint);
    var now = DateTimeOffset.UtcNow;
    var list = new CertificationRevocationList(1, deployed.Policy.CatalogVersion, 2, now, now.AddHours(1),
        [new(modelId, revision, request.Task, deployed.Runtime.Name, deployed.Digest)]);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(list, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var path = Path.Combine(outputPath, "signed-policy.revoked.json");
    var signature = Path.Combine(outputPath, "signed-policy.revoked.sig");
    await File.WriteAllBytesAsync(path, bytes);
    await File.WriteAllTextAsync(signature,
        Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    if (holdAfterStoreStage)
    {
        var storePath = Path.Combine(outputPath, "signed-policy.publication-store.json");
        var store = new FileRevocationPublicationStore(storePath);
        await store.StageAsync(new(2, bytes, await File.ReadAllBytesAsync(signature)));
        var probe = new StoreRecoveryProbe(deployed.CatalogPath, deployed.SignaturePath,
            Path.Combine(outputPath, "drill-public-key.pem"), platform, deployed.Runtime.Name, deployed.Digest,
            storePath, oldCheckpoint, outputPath, capabilities, maxLength, pooling, normalize);
        await File.WriteAllTextAsync(Path.Combine(outputPath, "store-recovery-probe.json"), JsonSerializer.Serialize(probe));
        Console.WriteLine("STORE_STAGE_READY");
        Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    await deployed.Policy.ApplyRevocationsAsync(path, signature);
    await MustBlock(async () => { await session.InvokeAsync(request); }, ModelScopeErrorCode.RuntimeNotInstalled);
    await MustBlock(async () => { await using var unexpected = await router.CreateSessionAsync(capabilities); },
        ModelScopeErrorCode.RuntimeNotInstalled);
    var checkpoint = Path.Combine(outputPath, "signed-policy.after.checkpoint.json");
    var anchor = await deployed.Policy.SaveRevocationCheckpointAsync(checkpoint);
    if (holdAfterCheckpoint)
    {
        var probe = new RecoveryProbe(deployed.CatalogPath, deployed.SignaturePath,
            Path.Combine(outputPath, "drill-public-key.pem"), platform, deployed.Runtime.Name, deployed.Digest,
            checkpoint, oldCheckpoint, anchor, capabilities, maxLength, pooling, normalize);
        await File.WriteAllTextAsync(Path.Combine(outputPath, "recovery-probe.json"), JsonSerializer.Serialize(probe));
        Console.WriteLine("CHECKPOINT_READY");
        Console.Out.Flush();
        // Only the external drill supervisor may end this owned process after recording the ready marker.
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    var restored = await ProductionRuntimePolicy.LoadWithRevocationCheckpointAsync(deployed.CatalogPath,
        deployed.SignaturePath, rsa.ExportSubjectPublicKeyInfoPem(), platform,
        new Dictionary<string, string> { [deployed.Runtime.Name] = deployed.Digest }, checkpoint, anchor);
    await MustBlock(async () => { await using var unexpected = await Router(restored).CreateSessionAsync(capabilities); },
        ModelScopeErrorCode.RuntimeNotInstalled);
    await MustBlock(async () =>
    {
        await ProductionRuntimePolicy.LoadWithRevocationCheckpointAsync(deployed.CatalogPath,
            deployed.SignaturePath, rsa.ExportSubjectPublicKeyInfoPem(), platform,
            new Dictionary<string, string> { [deployed.Runtime.Name] = deployed.Digest }, oldCheckpoint, anchor);
    }, ModelScopeErrorCode.DownloadIntegrityFailed);
    return new
    {
        status = "passed", preRevocationGoldPassed = true, loadedSessionBlocked = true,
        newSessionBlocked = true, checkpointRestoredSessionBlocked = true, oldCheckpointRejected = true,
        experimentalOverrideEnabled = true, anchor,
        limitation = "Explicit in-process policy recreation using a retained trusted anchor; not process-crash, automated distribution or durable anchor publication evidence."
    };
}

async Task<object> ExerciseDurablePublication()
{
    await CreateRelease("publication-policy", normalize);
    var deployed = deployments["publication-policy"];
    var router = new RuntimeRouter([deployed.Runtime], new()
    {
        Mode = RuntimePolicyMode.Production, ProductionPolicy = deployed.Policy, AllowExperimentalInProduction = true
    });
    await using var session = await router.CreateSessionAsync(capabilities);
    if (!IsGold(await session.InvokeAsync(request))) throw new InvalidDataException("Publication baseline gold failed.");
    // Empty update deliberately leaves this model unrevoked: only the publication gate can block it.
    var now = DateTimeOffset.UtcNow;
    var bytes = JsonSerializer.SerializeToUtf8Bytes(new CertificationRevocationList(1,
        deployed.Policy.CatalogVersion, 2, now, now.AddHours(1), []), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var listPath = Path.Combine(outputPath, "publication-policy.update.json");
    var signaturePath = Path.Combine(outputPath, "publication-policy.update.sig");
    await File.WriteAllBytesAsync(listPath, bytes);
    await File.WriteAllTextAsync(signaturePath, Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    var callbackReached = false;
    var failed = false;
    try
    {
        await deployed.Policy.ApplyAndPublishRevocationsAsync(listPath, signaturePath,
            Path.Combine(outputPath, "publication-policy.failed.checkpoint"), async (path, anchor, ct) =>
            {
                if (await HashFile(path) != anchor.Sha256) throw new InvalidDataException("Publisher received incomplete checkpoint.");
                callbackReached = true;
                await MustBlock(async () => { await session.InvokeAsync(request, ct); }, ModelScopeErrorCode.RuntimeNotInstalled);
                throw new IOException("Injected drill publisher failure");
            });
    }
    catch (IOException) when (callbackReached) { failed = true; }
    if (!failed) throw new InvalidDataException("Publisher fault was not observed.");
    await MustBlock(async () => { await session.InvokeAsync(request); }, ModelScopeErrorCode.RuntimeNotInstalled);
    await MustBlock(async () => { await using var unexpected = await router.CreateSessionAsync(capabilities); }, ModelScopeErrorCode.RuntimeNotInstalled);
    await deployed.Policy.SaveRevocationCheckpointAsync(Path.Combine(outputPath, "publication-policy.save-only.checkpoint"));
    await MustBlock(async () => { await session.InvokeAsync(request); }, ModelScopeErrorCode.RuntimeNotInstalled);
    var retryValidated = false;
    var retryPath = Path.Combine(outputPath, "publication-policy.retry.checkpoint");
    var published = await deployed.Policy.RetryRevocationPublicationAsync(retryPath, async (path, anchor, ct) =>
    {
        if (await HashFile(path) != anchor.Sha256 || anchor.Sequence != 2) throw new InvalidDataException("Retry checkpoint mismatch.");
        var recoveredPolicy = await ProductionRuntimePolicy.LoadWithRevocationCheckpointAsync(deployed.CatalogPath,
            deployed.SignaturePath, rsa.ExportSubjectPublicKeyInfoPem(), platform,
            new Dictionary<string, string> { [deployed.Runtime.Name] = deployed.Digest }, path, anchor, ct);
        if (!recoveredPolicy.IsCertified(capabilities, deployed.Runtime.Name)) throw new InvalidDataException("Retry recovery failed.");
        await MustBlock(async () => { await session.InvokeAsync(request, ct); }, ModelScopeErrorCode.RuntimeNotInstalled);
        // This is an acknowledged drill callback, not a production rollback-resistant anchor store.
        retryValidated = true;
    });
    if (!retryValidated || !IsGold(await session.InvokeAsync(request))) throw new InvalidDataException("Acknowledged publication failed to reopen original session.");
    await using var freshSession = await router.CreateSessionAsync(capabilities);
    if (!IsGold(await freshSession.InvokeAsync(request))) throw new InvalidDataException("Publication recovery new session gold failed.");
    return new
    {
        status = "passed", unrevokedModel = true, baselineGoldPassed = true, callbackReached,
        cachedSessionBlockedDuringPublish = true, failedPublicationBlocksCachedAndNewSessions = true,
        saveAloneCannotUnblock = true, retryCheckpointAndRecoveryValidated = retryValidated,
        originalAndNewSessionGoldAfterAcknowledgement = true, anchor = published,
        limitation = "Real BGE runtime with injected publisher exception and test acknowledgement; not a durable external anchor store, process-crash publication, or network distribution test."
    };
}

static async Task MustBlock(Func<Task> action, ModelScopeErrorCode expectedCode)
{
    try { await action(); }
    catch (ModelScopeException error) when (error.Code == expectedCode) { return; }
    throw new InvalidDataException("Signed revocation did not fail closed with the expected error.");
}

static async Task RestoreProbe(string descriptorPath)
{
    var probe = JsonSerializer.Deserialize<RecoveryProbe>(await File.ReadAllTextAsync(descriptorPath))
        ?? throw new InvalidDataException("Missing recovery probe descriptor.");
    var publicKey = await File.ReadAllTextAsync(probe.PublicKeyPath);
    var digests = new Dictionary<string, string> { [probe.Runtime] = probe.Digest };
    var policy = await ProductionRuntimePolicy.LoadWithRevocationCheckpointAsync(probe.CatalogPath,
        probe.SignaturePath, publicKey, probe.Platform, digests, probe.Checkpoint, probe.Anchor);
    var runtime = new OnnxEmbeddingRuntime(new OnnxRuntimeAdapter(new() { ModelFile = "onnx/model.onnx" }),
        new() { MaxLength = probe.MaxLength, Pooling = probe.Pooling, Normalize = probe.Normalize });
    var router = new RuntimeRouter([runtime], new()
    {
        Mode = RuntimePolicyMode.Production, ProductionPolicy = policy, AllowExperimentalInProduction = true
    });
    await MustBlock(async () => { await using var unexpected = await router.CreateSessionAsync(probe.Capabilities); },
        ModelScopeErrorCode.RuntimeNotInstalled);
    await MustBlock(async () =>
    {
        await ProductionRuntimePolicy.LoadWithRevocationCheckpointAsync(probe.CatalogPath, probe.SignaturePath,
            publicKey, probe.Platform, digests, probe.OldCheckpoint, probe.Anchor);
    }, ModelScopeErrorCode.DownloadIntegrityFailed);
    Console.WriteLine("RECOVERY_BLOCKED_REVOKED_AND_OLD_CHECKPOINT");
}

static async Task RestoreStoreProbe(string descriptorPath)
{
    var probe = JsonSerializer.Deserialize<StoreRecoveryProbe>(await File.ReadAllTextAsync(descriptorPath))
        ?? throw new InvalidDataException("Missing store recovery probe descriptor.");
    var publicKey = await File.ReadAllTextAsync(probe.PublicKeyPath);
    var digests = new Dictionary<string, string> { [probe.Runtime] = probe.Digest };
    var store = new FileRevocationPublicationStore(probe.StorePath);
    var policy = await ProductionRuntimePolicy.LoadWithRevocationPublicationStoreAsync(
        probe.CatalogPath, probe.SignaturePath, publicKey, probe.Platform, digests, store,
        sequence => Path.Combine(probe.OutputPath, $"store-reconciled-{sequence}-{Guid.NewGuid():N}.checkpoint"));
    var runtime = new OnnxEmbeddingRuntime(new OnnxRuntimeAdapter(new() { ModelFile = "onnx/model.onnx" }),
        new() { MaxLength = probe.MaxLength, Pooling = probe.Pooling, Normalize = probe.Normalize });
    var router = new RuntimeRouter([runtime], new()
    {
        Mode = RuntimePolicyMode.Production, ProductionPolicy = policy, AllowExperimentalInProduction = true
    });
    await MustBlock(async () => { await using var unexpected = await router.CreateSessionAsync(probe.Capabilities); },
        ModelScopeErrorCode.RuntimeNotInstalled);
    var state = await store.ReadAsync();
    if (state.Pending is not null || state.Committed?.Anchor.Sequence != 2)
        throw new InvalidDataException("Staged publication was not committed during startup reconciliation.");
    await MustBlock(async () =>
    {
        await ProductionRuntimePolicy.LoadWithRevocationCheckpointAsync(probe.CatalogPath, probe.SignaturePath,
            publicKey, probe.Platform, digests, probe.OldCheckpoint, state.Committed.Anchor);
    }, ModelScopeErrorCode.DownloadIntegrityFailed);
    Console.WriteLine("STORE_RECONCILED_AND_REVOKED_MODEL_BLOCKED");
}

static async Task<string> HashFile(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}

internal sealed record RecoveryProbe(string CatalogPath, string SignaturePath, string PublicKeyPath,
    string Platform, string Runtime, string Digest, string Checkpoint, string OldCheckpoint,
    RevocationCheckpointAnchor Anchor, ModelCapabilities Capabilities, int MaxLength,
    OnnxEmbeddingPooling Pooling, bool Normalize);

internal sealed record StoreRecoveryProbe(string CatalogPath, string SignaturePath, string PublicKeyPath,
    string Platform, string Runtime, string Digest, string StorePath, string OldCheckpoint, string OutputPath,
    ModelCapabilities Capabilities, int MaxLength, OnnxEmbeddingPooling Pooling, bool Normalize);
