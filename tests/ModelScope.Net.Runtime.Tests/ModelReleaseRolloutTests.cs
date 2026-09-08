using System.Security.Cryptography;
using System.Text.Json;
using ModelScope.Net.Runtime;
using ModelScope.Net.Runtime.Onnx;

namespace ModelScope.Net.Runtime.Tests;

public sealed class ModelReleaseRolloutTests
{
    private static readonly ModelRequest Request = new("raw-onnx",
        JsonSerializer.SerializeToElement(new { inputs = new { x = new[] { 1f, 2f }, y = new[] { 3f, 4f } } }));
    private static readonly ModelRolloutPolicy FastPolicy = new(1, TimeSpan.Zero, TimeSpan.FromMinutes(1));

    [Fact]
    public async Task RealOnnx_CanaryAllStagesPromoteRollbackAndRetainedSession()
    {
        using var fixture = new Fixture();
        var stable = await fixture.ReleaseAsync("stable", 'a', new OnnxRuntimeAdapter());
        var candidate = await fixture.ReleaseAsync("candidate", 'b', new OnnxRuntimeAdapter());
        var rollout = new ModelReleaseRollout(stable, FastPolicy);
        await rollout.StageAsync(candidate, Request, response =>
            response.Output.GetProperty("outputs").GetProperty("sum").GetProperty("data")[0].GetSingle() == 4);
        foreach (var percent in new[] { 5, 25, 50, 100 })
        {
            Assert.Equal(percent, rollout.GetSnapshot().CandidatePercent);
            Assert.Throws<InvalidOperationException>(rollout.Advance);
            var result = await rollout.InvokeAsync(CanaryKey(), Request);
            Assert.Equal(new string('b', 40), result.Revision);
            rollout.Advance();
        }
        Assert.Equal("candidate", rollout.GetSnapshot().StableRelease);
        Assert.Equal("stable", rollout.GetSnapshot().PreviousRelease);
        rollout.Rollback();
        var restored = await rollout.InvokeAsync(CanaryKey(), Request);
        Assert.Equal(new string('a', 40), restored.Revision);
        Assert.Equal("operator-rollback", rollout.GetSnapshot().LastRollbackReason);
    }

    [Fact]
    public async Task RuntimeFailureAbortsCanary_StableTrafficDoesNotCount()
    {
        using var fixture = new Fixture();
        var runtime = new ControlledRuntime();
        var stable = await fixture.ReleaseAsync("stable", 'a', new ControlledRuntime());
        var rollout = new ModelReleaseRollout(stable, FastPolicy);
        await rollout.StageAsync(await fixture.ReleaseAsync("candidate", 'b', runtime), Request, _ => true);
        await rollout.InvokeAsync(StableKey(), Request);
        Assert.Equal(0, rollout.GetSnapshot().SuccessfulRequests);
        runtime.Fail = true;
        await Assert.ThrowsAsync<ModelScopeException>(() => rollout.InvokeAsync(CanaryKey(), Request));
        Assert.Null(rollout.GetSnapshot().CandidateRelease);
        Assert.Equal("runtime-failure", rollout.GetSnapshot().LastRollbackReason);
        Assert.Equal(new string('a', 40), (await rollout.InvokeAsync(CanaryKey(), Request)).Revision);
    }

    [Fact]
    public async Task RollbackDoesNotDisposeInFlightRequest_AndIgnoresItsLateResult()
    {
        using var fixture = new Fixture();
        var runtime = new ControlledRuntime();
        var stable = await fixture.ReleaseAsync("stable", 'a', new ControlledRuntime());
        var rollout = new ModelReleaseRollout(stable, FastPolicy);
        await rollout.StageAsync(await fixture.ReleaseAsync("candidate", 'b', runtime), Request, _ => true);
        runtime.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = rollout.InvokeAsync(CanaryKey(), Request);
        rollout.Rollback();
        Assert.Equal(new string('a', 40), (await rollout.InvokeAsync(CanaryKey(), Request)).Revision);
        await rollout.StageAsync(await fixture.ReleaseAsync("next", 'c', new ControlledRuntime()), Request, _ => true);
        runtime.Gate.SetResult();
        Assert.Equal(new string('b', 40), (await inFlight).Revision);
        Assert.Equal(0, rollout.GetSnapshot().SuccessfulRequests);
        Assert.Equal("next", rollout.GetSnapshot().CandidateRelease);
        Assert.Equal(2, runtime.DisposedSessions); // Preflight and the completed in-flight call.
    }

    [Fact]
    public async Task FailedProbeAndCancelledRequestNeverPromote()
    {
        using var fixture = new Fixture();
        var stable = await fixture.ReleaseAsync("stable", 'a', new ControlledRuntime());
        var runtime = new ControlledRuntime();
        var candidate = await fixture.ReleaseAsync("candidate", 'b', runtime);
        var rollout = new ModelReleaseRollout(stable, FastPolicy);
        await Assert.ThrowsAsync<ModelScopeException>(() => rollout.StageAsync(candidate, Request, _ => false));
        Assert.Null(rollout.GetSnapshot().CandidateRelease);
        await rollout.StageAsync(candidate, Request, _ => true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rollout.InvokeAsync(CanaryKey(), Request, cancellation.Token));
        Assert.Equal("candidate", rollout.GetSnapshot().CandidateRelease);
        Assert.Equal(0, rollout.GetSnapshot().SuccessfulRequests);
        Assert.Throws<InvalidOperationException>(rollout.Advance);
    }

    [Fact]
    public async Task StreamingMustFinishAndEmitTerminal_AndRollbackPreservesExistingStream()
    {
        using var fixture = new Fixture();
        var runtime = new ControlledRuntime();
        var stable = await fixture.ReleaseAsync("stable", 'a', new ControlledRuntime());
        var rollout = new ModelReleaseRollout(stable, FastPolicy);
        await rollout.StageAsync(await fixture.ReleaseAsync("candidate", 'b', runtime), Request, _ => true);
        await using (var iterator = rollout.InvokeStreamingAsync(CanaryKey(), Request).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync());
            rollout.Rollback();
            Assert.True(await iterator.MoveNextAsync());
            Assert.True(iterator.Current.IsTerminal);
            Assert.False(await iterator.MoveNextAsync());
        }
        Assert.Equal("stable", rollout.GetSnapshot().StableRelease);
        await rollout.StageAsync(await fixture.ReleaseAsync("next", 'c', runtime), Request, _ => true);
        runtime.EmitTerminal = false;
        await Assert.ThrowsAsync<ModelScopeException>(async () =>
        {
            await foreach (var item in rollout.InvokeStreamingAsync(CanaryKey(), Request)) { }
        });
        Assert.Null(rollout.GetSnapshot().CandidateRelease);
    }

    [Fact]
    public async Task ObservationTimeCannotBeReplacedByFastRepeatedRequests()
    {
        using var fixture = new Fixture();
        var stable = await fixture.ReleaseAsync("stable", 'a', new ControlledRuntime());
        var rollout = new ModelReleaseRollout(stable, new(1, TimeSpan.FromHours(1)));
        await rollout.StageAsync(await fixture.ReleaseAsync("candidate", 'b', new ControlledRuntime()), Request, _ => true);
        await rollout.InvokeAsync(CanaryKey(), Request);
        Assert.Throws<InvalidOperationException>(rollout.Advance);
    }

    [Fact]
    public async Task SignedCatalogRejectsWrongRevisionPlatformStatusAndTamperedArtifact()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ModelScopeException>(() =>
            fixture.ReleaseAsync("bad-platform", 'a', new ControlledRuntime(), platform: "unapproved"));
        await Assert.ThrowsAsync<ModelScopeException>(() =>
            fixture.ReleaseAsync("bad-revision", 'b', new ControlledRuntime(), catalogRevision: new string('c', 40)));
        await Assert.ThrowsAsync<ModelScopeException>(() =>
            fixture.ReleaseAsync("uncertified", 'a', new ControlledRuntime(), status: CapabilityStatus.Compatible));
        await Assert.ThrowsAsync<ModelScopeException>(() =>
            fixture.ReleaseAsync("tampered", 'a', new ControlledRuntime(), tamper: true));
    }

    [Fact]
    public async Task RevocationAbortsCandidateAndFailsClosedWhenNoSafeStableExists()
    {
        using var fixture = new Fixture();
        var stable = await fixture.ReleaseAsync("stable", 'a', new ControlledRuntime());
        var rollout = new ModelReleaseRollout(stable, FastPolicy);
        var candidate = await fixture.ReleaseAsync("candidate", 'b', new ControlledRuntime());
        await rollout.StageAsync(candidate, Request, _ => true);
        rollout.Revoke("candidate");
        Assert.Null(rollout.GetSnapshot().CandidateRelease);
        Assert.Equal("certification-revoked", rollout.GetSnapshot().LastRollbackReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rollout.StageAsync(candidate, Request, _ => true));
        rollout.Revoke("stable");
        await Assert.ThrowsAsync<ModelScopeException>(() => rollout.InvokeAsync(StableKey(), Request));
    }

    private static string CanaryKey() => KeyFor(canary: true);
    private static string StableKey() => KeyFor(canary: false);
    private static string KeyFor(bool canary)
    {
        for (var i = 0; ; i++)
        {
            var key = $"cohort-{i}";
            var bucket = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))) % 100;
            if ((bucket < 5) == canary) return key;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"modelscope-rollout-{Guid.NewGuid():N}");
        private readonly RSA _rsa = RSA.Create(2048);

        public Fixture() => Directory.CreateDirectory(_directory);

        public async Task<VerifiedModelRelease> ReleaseAsync(string id, char revision, IModelRuntime runtime,
            string platform = "test-cpu", string? catalogRevision = null,
            CapabilityStatus status = CapabilityStatus.Certified, bool tamper = false)
        {
            var directory = Path.Combine(_directory, id);
            Directory.CreateDirectory(directory);
            var model = Convert.FromBase64String(
                "CAoSFE1vZGVsU2NvcGUuTmV0LlRlc3RzOlgKEAoBeAoBeRIDc3VtIgNBZGQSD2FkZC10d28tdmVjdG9yc1oPCgF4EgoKCAgBEgQKAggCWg8KAXkSCgoICAESBAoCCAJiEQoDc3VtEgoKCAgBEgQKAggCQgQKABAN");
            await File.WriteAllBytesAsync(Path.Combine(directory, "model.onnx"), model);
            var catalog = new CompatibilityCatalog(1, id, DateTimeOffset.UtcNow,
                [new("tests/add-vectors", catalogRevision ?? new string(revision, 40), "raw-onnx", runtime.Name, status,
                    new Dictionary<string, string> { ["model.onnx"] = Convert.ToHexStringLower(SHA256.HashData(model)) },
                    ["test-cpu"], "sha256:" + new string('d', 64))]);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var catalogPath = Path.Combine(directory, "catalog.json");
            var signaturePath = Path.Combine(directory, "catalog.sig");
            await File.WriteAllBytesAsync(catalogPath, bytes);
            await File.WriteAllTextAsync(signaturePath,
                Convert.ToBase64String(_rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
            if (tamper) await File.WriteAllBytesAsync(Path.Combine(directory, "model.onnx"), [0]);
            var capabilities = new ModelCapabilities(directory, "tests/add-vectors", new string(revision, 40),
                "raw-onnx", [], [new("model.onnx", ModelArtifactFormat.Onnx, model.Length)], [], false, []);
            // Synthetic graph tests exercise actual ONNX while separate catalog gates enforce authorization.
            return await VerifiedModelRelease.CreateAsync(id, catalogPath, signaturePath,
                _rsa.ExportSubjectPublicKeyInfoPem(), capabilities, runtime.Name, "sha256:" + new string('d', 64),
                platform, new RuntimeRouter([runtime]));
        }

        public void Dispose()
        {
            _rsa.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ControlledRuntime : IModelRuntime
    {
        public string Name => "test";
        public bool Fail { get; set; }
        public bool EmitTerminal { get; set; } = true;
        public int DisposedSessions { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public RuntimeCandidate Evaluate(ModelCapabilities capabilities) =>
            new(Name, CapabilityStatus.Certified, "fixture", 100);
        public Task<IModelSession> CreateSessionAsync(ModelCapabilities capabilities, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IModelSession>(new Session(this, capabilities));
        }

        private sealed class Session(ControlledRuntime runtime, ModelCapabilities capabilities) : IModelSession
        {
            public ModelCapabilities Capabilities => capabilities;
            public async Task<ModelResponse> InvokeAsync(ModelRequest request, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (runtime.Gate is not null) await runtime.Gate.Task.WaitAsync(cancellationToken);
                if (runtime.Fail) throw new ModelScopeException(ModelScopeErrorCode.InferenceFailed, "fixture");
                return new(JsonSerializer.SerializeToElement(new { ok = true }), capabilities.ModelId!,
                    capabilities.Revision!, runtime.Name, TimeSpan.Zero);
            }
            public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(ModelRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return new("chunk", JsonSerializer.SerializeToElement("ok"));
                if (runtime.EmitTerminal) yield return new("done", JsonSerializer.SerializeToElement("done"), true);
            }
            public ValueTask DisposeAsync()
            {
                runtime.DisposedSessions++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
