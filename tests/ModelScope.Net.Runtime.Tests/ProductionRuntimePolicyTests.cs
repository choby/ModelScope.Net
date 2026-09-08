using System.Security.Cryptography;
using System.Text.Json;
using ModelScope.Net.Runtime;

namespace ModelScope.Net.Runtime.Tests;

public sealed class ProductionRuntimePolicyTests
{
    [Fact]
    public async Task StoreBackedUpdateBlocksBeforeDurableStageAcknowledgement()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        await fixture.RevokeAsync(policy, 1, [], prepareOnly: true);
        var store = new BlockingPublicationStore();
        var update = policy.ApplyAndPublishRevocationsAsync(
            Path.Combine(fixture.Directory, "revocations.json"),
            Path.Combine(fixture.Directory, "revocations.sig"),
            Path.Combine(fixture.Directory, "store-backed.checkpoint"), store);

        await store.StageEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(policy.IsCertified(Capabilities(fixture.Directory), "onnx"));
        store.ReleaseStage.SetResult();
        var anchor = await update;

        Assert.Equal(2, anchor.Sequence);
        Assert.True(policy.IsCertified(Capabilities(fixture.Directory), "onnx"));
        Assert.NotNull((await store.ReadAsync()).Committed);
    }

    [Fact]
    public async Task PublicationStoreReconcilesStagedUpdateAfterProcessLoss()
    {
        using var fixture = new Fixture();
        await fixture.RevokeAsync(await fixture.PolicyAsync(initializeRevocations: false), 0,
            [TargetRevocation()], prepareOnly: true);
        var store = new FileRevocationPublicationStore(Path.Combine(fixture.Directory, "publication-store.json"));
        var document = await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "revocations.json"));
        var signature = await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "revocations.sig"));
        await store.StageAsync(new(1, document, signature));

        var restored = await ProductionRuntimePolicy.LoadWithRevocationPublicationStoreAsync(
            fixture.CatalogPath, fixture.SignaturePath, fixture.PublicKey, "test-cpu",
            new Dictionary<string, string> { ["onnx"] = Digest }, store,
            sequence => Path.Combine(fixture.Directory, $"reconciled-{sequence}-{Guid.NewGuid():N}.checkpoint"));

        Assert.True(restored.IsRevokedOrExpired(Capabilities(fixture.Directory), "onnx"));
        var state = await store.ReadAsync();
        Assert.Null(state.Pending);
        Assert.Equal(1, state.Committed?.Anchor.Sequence);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RevokeAsync(restored, 1, []));
    }

    [Fact]
    public async Task FilePublicationStoreRestoresCommittedCheckpointAndRejectsRollback()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        await fixture.RevokeAsync(policy, 1, [TargetRevocation()], prepareOnly: true);
        var store = new FileRevocationPublicationStore(Path.Combine(fixture.Directory, "publication-store.json"));
        var checkpoint = Path.Combine(fixture.Directory, "committed.checkpoint");
        var anchor = await policy.ApplyAndPublishRevocationsAsync(
            Path.Combine(fixture.Directory, "revocations.json"),
            Path.Combine(fixture.Directory, "revocations.sig"), checkpoint, store);

        var restored = await ProductionRuntimePolicy.LoadWithRevocationPublicationStoreAsync(
            fixture.CatalogPath, fixture.SignaturePath, fixture.PublicKey, "test-cpu",
            new Dictionary<string, string> { ["onnx"] = Digest }, store,
            _ => throw new InvalidOperationException("No pending update should require reconciliation."));
        Assert.True(restored.IsRevokedOrExpired(Capabilities(fixture.Directory), "onnx"));

        var state = await store.ReadAsync();
        Assert.Equal(anchor, state.Committed?.Anchor);
        var stale = new PendingRevocationPublication(anchor.Sequence,
            await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "revocations.json")),
            await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "revocations.sig")));
        await Assert.ThrowsAsync<ModelScopeException>(() => store.StageAsync(stale));
    }

    [Fact]
    public async Task StoreCommitAcknowledgementLossRemainsBlockedAndCanRetryIdempotently()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        await fixture.RevokeAsync(policy, 1, [], prepareOnly: true);
        var durable = new FileRevocationPublicationStore(Path.Combine(fixture.Directory, "publication-store.json"));
        var store = new CommitThenThrowPublicationStore(durable);

        await Assert.ThrowsAsync<IOException>(() => policy.ApplyAndPublishRevocationsAsync(
            Path.Combine(fixture.Directory, "revocations.json"),
            Path.Combine(fixture.Directory, "revocations.sig"),
            Path.Combine(fixture.Directory, "first-commit.checkpoint"), store));
        Assert.False(policy.IsCertified(Capabilities(fixture.Directory), "onnx"));

        var anchor = await policy.RetryRevocationPublicationAsync(
            Path.Combine(fixture.Directory, "retry-commit.checkpoint"), store);
        Assert.Equal(2, anchor.Sequence);
        Assert.True(policy.IsCertified(Capabilities(fixture.Directory), "onnx"));
        var state = await durable.ReadAsync();
        Assert.Null(state.Pending);
        Assert.Equal(anchor, state.Committed?.Anchor);
    }

    [Fact]
    public async Task DurableUpdatesSerializeAndCancelledWaiterCannotClearPendingPublication()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        await fixture.RevokeAsync(policy, 1, [], prepareOnly: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = policy.ApplyAndPublishRevocationsAsync(Path.Combine(fixture.Directory, "revocations.json"),
            Path.Combine(fixture.Directory, "revocations.sig"), Path.Combine(fixture.Directory, "first.checkpoint"),
            async (_, _, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancelled = new CancellationTokenSource();
        var waiting = policy.RetryRevocationPublicationAsync(Path.Combine(fixture.Directory, "cancelled.checkpoint"),
            (_, _, _) => throw new InvalidOperationException("Cancelled waiter must not publish"), cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(policy.IsCertified(Capabilities(fixture.Directory), "onnx"));
        await fixture.RevokeAsync(policy, 2, [TargetRevocation()], prepareOnly: true);
        var second = policy.ApplyAndPublishRevocationsAsync(Path.Combine(fixture.Directory, "revocations.json"),
            Path.Combine(fixture.Directory, "revocations.sig"), Path.Combine(fixture.Directory, "second.checkpoint"),
            (_, _, _) => Task.CompletedTask);
        Assert.False(second.IsCompleted);
        release.SetResult();
        Assert.Equal(2, (await first).Sequence);
        Assert.Equal(3, (await second).Sequence);
        Assert.True(policy.IsRevokedOrExpired(Capabilities(fixture.Directory), "onnx"));
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "cancelled.checkpoint")));
    }

    [Fact]
    public async Task DurablePublicationBlocksUntilTrustedPublisherAcknowledgesAndDisallowsLegacyUpdates()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        await fixture.RevokeAsync(policy, 1, [], prepareOnly: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = policy.ApplyAndPublishRevocationsAsync(Path.Combine(fixture.Directory, "revocations.json"),
            Path.Combine(fixture.Directory, "revocations.sig"), Path.Combine(fixture.Directory, "durable.checkpoint"),
            async (path, anchor, ct) =>
            {
                var restored = await fixture.RestoreAsync(path, anchor);
                Assert.True(restored.IsCertified(Capabilities(fixture.Directory), "onnx"));
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(policy.IsCertified(Capabilities(fixture.Directory), "onnx"));
        Assert.True(policy.IsRevokedOrExpired(Capabilities(fixture.Directory) with { ModelId = null }, "unknown"));
        release.SetResult();
        var anchor = await update;
        Assert.Equal(2, anchor.Sequence);
        Assert.True(policy.IsCertified(Capabilities(fixture.Directory), "onnx"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RevokeAsync(policy, 2, []));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("publish")]
    [InlineData("cancel")]
    public async Task DurableFailureStaysClosedUntilRetryAndRetainsRevocations(string failure)
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        await fixture.RevokeAsync(policy, 1, [TargetRevocation()], prepareOnly: true);
        var path = Path.Combine(fixture.Directory, "failed.checkpoint");
        if (failure == "write") await File.WriteAllTextAsync(path, "existing");
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<Exception>(() => policy.ApplyAndPublishRevocationsAsync(
            Path.Combine(fixture.Directory, "revocations.json"), Path.Combine(fixture.Directory, "revocations.sig"), path,
            (_, _, _) =>
            {
                if (failure == "cancel") { cancelled.Cancel(); return Task.CompletedTask; }
                throw new IOException("fixture publisher failure");
            }, cancelled.Token));
        var unrelated = Capabilities(fixture.Directory) with { Revision = new string('f', 40) };
        Assert.True(policy.IsRevokedOrExpired(unrelated, "onnx"));
        // Saving alone cannot bypass a failed trusted publication.
        await policy.SaveRevocationCheckpointAsync(Path.Combine(fixture.Directory, "save-only.checkpoint"));
        Assert.True(policy.IsRevokedOrExpired(unrelated, "onnx"));
        var anchor = await policy.RetryRevocationPublicationAsync(Path.Combine(fixture.Directory, "retry.checkpoint"),
            (_, _, _) => Task.CompletedTask);
        Assert.Equal(2, anchor.Sequence);
        Assert.False(policy.IsRevokedOrExpired(unrelated, "onnx"));
        Assert.True(policy.IsRevokedOrExpired(Capabilities(fixture.Directory), "onnx"));
        var restored = await fixture.RestoreAsync(Path.Combine(fixture.Directory, "retry.checkpoint"), anchor);
        Assert.True(restored.IsRevokedOrExpired(Capabilities(fixture.Directory), "onnx"));
    }

    [Fact]
    public async Task InvalidDurableUpdateDoesNotChangeExistingAuthorization()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        await fixture.RevokeAsync(policy, 1, [], tamper: true, prepareOnly: true);
        var called = false;
        await Assert.ThrowsAsync<ModelScopeException>(() => policy.ApplyAndPublishRevocationsAsync(
            Path.Combine(fixture.Directory, "revocations.json"), Path.Combine(fixture.Directory, "revocations.sig"),
            Path.Combine(fixture.Directory, "invalid.checkpoint"), (_, _, _) => { called = true; return Task.CompletedTask; }));
        Assert.False(called);
        Assert.True(policy.IsCertified(Capabilities(fixture.Directory), "onnx"));
    }

    [Fact]
    public async Task CheckpointRestoresRevocationUnionAndRejectsOldGeneration()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        var oldPath = Path.Combine(fixture.Directory, "old.checkpoint");
        await policy.SaveRevocationCheckpointAsync(oldPath);
        await fixture.RevokeAsync(policy, 1, [TargetRevocation()]);
        await fixture.RevokeAsync(policy, 2, []);
        var path = Path.Combine(fixture.Directory, "current.checkpoint");
        var anchor = await policy.SaveRevocationCheckpointAsync(path);
        var restored = await fixture.RestoreAsync(path, anchor);
        Assert.False(restored.IsCertified(Capabilities(fixture.Directory), "onnx"));
        await Assert.ThrowsAsync<ModelScopeException>(() => fixture.RevokeAsync(restored, 2, []));
        await fixture.RevokeAsync(restored, 3, []);
        Assert.False(restored.IsCertified(Capabilities(fixture.Directory), "onnx"));
        await Assert.ThrowsAsync<ModelScopeException>(() => fixture.RestoreAsync(oldPath, anchor));
        await Assert.ThrowsAsync<ModelScopeException>(() => policy.SaveRevocationCheckpointAsync(path));
    }

    [Fact]
    public async Task CheckpointTamperingMissingFileAndWrongSequenceFailClosed()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        var path = Path.Combine(fixture.Directory, "checkpoint");
        var anchor = await policy.SaveRevocationCheckpointAsync(path);
        await Assert.ThrowsAsync<ModelScopeException>(() => fixture.RestoreAsync(path, anchor with { Sequence = anchor.Sequence + 1 }));
        await Assert.ThrowsAsync<ModelScopeException>(() => fixture.RestoreAsync(path + ".missing", anchor));
        await File.AppendAllTextAsync(path, " ");
        await Assert.ThrowsAsync<ModelScopeException>(() => fixture.RestoreAsync(path, anchor));
    }

    [Fact]
    public async Task ExpiredCheckpointRetainsRevocationsWhileAwaitingFreshFeed()
    {
        using var fixture = new Fixture();
        var clock = new RevocationClock();
        var policy = await fixture.PolicyAsync(clock: clock);
        await fixture.RevokeAsync(policy, 1, [TargetRevocation()], time: clock.GetUtcNow());
        var path = Path.Combine(fixture.Directory, "checkpoint");
        var anchor = await policy.SaveRevocationCheckpointAsync(path);
        clock.Advance(TimeSpan.FromHours(2));
        var restored = await fixture.RestoreAsync(path, anchor, clock);
        var unrelated = Capabilities(fixture.Directory) with { Revision = new string('f', 40) };
        Assert.True(restored.IsRevokedOrExpired(unrelated, "onnx"));
        await fixture.RevokeAsync(restored, 2, [], time: clock.GetUtcNow());
        Assert.False(restored.IsRevokedOrExpired(unrelated, "onnx"));
        Assert.True(restored.IsRevokedOrExpired(Capabilities(fixture.Directory), "onnx"));
    }

    [Fact]
    public async Task CheckpointHashCannotReplaceOriginalSignatureVerification()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        var path = Path.Combine(fixture.Directory, "checkpoint");
        var anchor = await policy.SaveRevocationCheckpointAsync(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["Records"]![0]!["Signature"] = Convert.ToBase64String("invalid signature"u8);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        await File.WriteAllBytesAsync(path, bytes);
        var changedAnchor = anchor with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        await Assert.ThrowsAsync<ModelScopeException>(() => fixture.RestoreAsync(path, changedAnchor));
    }

    [Theory]
    [InlineData("onnx")]
    [InlineData("unregistered")]
    public async Task MissingAndExpiredFeedBlockAllRuntimesDespiteExperimentalOptIn(string name)
    {
        using var fixture = new Fixture();
        var clock = new RevocationClock();
        var policy = await fixture.PolicyAsync(clock: clock, initializeRevocations: false);
        var runtime = new RecordingRuntime(name);
        var router = new RuntimeRouter([runtime], new()
        {
            Mode = RuntimePolicyMode.Production, ProductionPolicy = policy, AllowExperimentalInProduction = true
        });
        var cap = Capabilities(fixture.Directory);
        await Assert.ThrowsAsync<ModelScopeException>(() => router.CreateSessionAsync(cap));
        Assert.Equal(0, runtime.Loads);
        await Assert.ThrowsAsync<ModelScopeException>(() => fixture.RevokeAsync(policy, 0, [], tamper: true, time: clock.GetUtcNow()));
        await Assert.ThrowsAsync<ModelScopeException>(() => router.CreateSessionAsync(cap));
        await fixture.RevokeAsync(policy, 0, [], time: clock.GetUtcNow());
        await using var session = await router.CreateSessionAsync(cap);
        var request = new ModelRequest("raw-onnx", JsonSerializer.SerializeToElement("input"));
        await session.InvokeAsync(request);
        clock.Advance(TimeSpan.FromHours(2));
        await Assert.ThrowsAsync<ModelScopeException>(() => session.InvokeAsync(request));
        await Assert.ThrowsAsync<ModelScopeException>(() => router.CreateSessionAsync(cap));
        Assert.True(policy.IsRevokedOrExpired(cap with { ModelId = null, Revision = null }, name));
        Assert.Equal(1, runtime.Loads);
    }

    [Fact]
    public async Task SignedRevocationBlocksCachedAndNewSessionsEvenWithExperimentalOverride()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        var runtime = new RecordingRuntime("onnx");
        var router = new RuntimeRouter([runtime], new()
        {
            Mode = RuntimePolicyMode.Production, ProductionPolicy = policy, AllowExperimentalInProduction = true
        });
        await using var pool = new ModelSessionPool(router);
        var cap = Capabilities(fixture.Directory);
        await using var lease = await pool.AcquireAsync(new(cap, 1, "onnx"));
        var request = new ModelRequest("raw-onnx", JsonSerializer.SerializeToElement("input"));
        await lease.Session.InvokeAsync(request);
        await fixture.RevokeAsync(policy, 1, [TargetRevocation()]);
        await Assert.ThrowsAsync<ModelScopeException>(() => lease.Session.InvokeAsync(request));
        await Assert.ThrowsAsync<ModelScopeException>(() => router.CreateSessionAsync(cap));
        Assert.Equal(1, runtime.Loads);
    }

    [Fact]
    public async Task DeferredStreamRechecksRevocation()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        var router = new RuntimeRouter([new RecordingRuntime("onnx")],
            new() { Mode = RuntimePolicyMode.Production, ProductionPolicy = policy });
        await using var session = await router.CreateSessionAsync(Capabilities(fixture.Directory));
        var request = new ModelRequest("raw-onnx", JsonSerializer.SerializeToElement("input"));
        var deferred = session.InvokeStreamingAsync(request);
        await fixture.RevokeAsync(policy, 1, [TargetRevocation()]);
        await using var iterator = deferred.GetAsyncEnumerator();
        await Assert.ThrowsAsync<ModelScopeException>(async () => await iterator.MoveNextAsync());
    }

    [Fact]
    public async Task RevocationsRejectReplayAndCannotBeClearedByOmission()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        await fixture.RevokeAsync(policy, 2, [TargetRevocation()]);
        await Assert.ThrowsAsync<ModelScopeException>(() => fixture.RevokeAsync(policy, 1, []));
        await Assert.ThrowsAsync<ModelScopeException>(() => fixture.RevokeAsync(policy, 2, []));
        await fixture.RevokeAsync(policy, 3, []);
        Assert.False(policy.IsCertified(Capabilities(fixture.Directory), "onnx"));
    }

    [Fact]
    public async Task WrongScopeTamperingFutureAndExpiredListsCannotMutateAuthorization()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        await Assert.ThrowsAsync<ModelScopeException>(() => fixture.RevokeAsync(policy, 1,
            [TargetRevocation()], scope: "other-catalog"));
        await Assert.ThrowsAsync<ModelScopeException>(() => fixture.RevokeAsync(policy, 1,
            [TargetRevocation()], tamper: true));
        await Assert.ThrowsAsync<ModelScopeException>(() => fixture.RevokeAsync(policy, 1,
            [TargetRevocation()], offset: TimeSpan.FromHours(-2)));
        await Assert.ThrowsAsync<ModelScopeException>(() => fixture.RevokeAsync(policy, 1,
            [TargetRevocation()], offset: TimeSpan.FromHours(2)));
        Assert.True(policy.IsCertified(Capabilities(fixture.Directory), "onnx"));
        await fixture.RevokeAsync(policy, 1, [TargetRevocation() with { Revision = new string('f', 40) }]);
        Assert.True(policy.IsCertified(Capabilities(fixture.Directory), "onnx"));
    }

    [Fact]
    public async Task ExpiredFeedBlocksExistingSessionUntilFreshFeedArrives()
    {
        using var fixture = new Fixture();
        var clock = new RevocationClock();
        var policy = await fixture.PolicyAsync(clock: clock);
        var router = new RuntimeRouter([new RecordingRuntime("onnx")],
            new() { Mode = RuntimePolicyMode.Production, ProductionPolicy = policy });
        await using var session = await router.CreateSessionAsync(Capabilities(fixture.Directory));
        await fixture.RevokeAsync(policy, 1, [], time: clock.GetUtcNow());
        clock.Advance(TimeSpan.FromHours(2));
        var request = new ModelRequest("raw-onnx", JsonSerializer.SerializeToElement("input"));
        await Assert.ThrowsAsync<ModelScopeException>(() => session.InvokeAsync(request));
        await fixture.RevokeAsync(policy, 2, [], time: clock.GetUtcNow());
        Assert.Equal("ok", (await session.InvokeAsync(request)).Output.GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationSuppressesInFlightResponseAndNextStreamEvent(bool streaming)
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        var runtime = new RecordingRuntime("onnx") { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var router = new RuntimeRouter([runtime],
            new() { Mode = RuntimePolicyMode.Production, ProductionPolicy = policy });
        await using var session = await router.CreateSessionAsync(Capabilities(fixture.Directory));
        var request = new ModelRequest("raw-onnx", JsonSerializer.SerializeToElement("input"));
        if (streaming)
        {
            await using var iterator = session.InvokeStreamingAsync(request).GetAsyncEnumerator();
            Assert.True(await iterator.MoveNextAsync());
            var pending = iterator.MoveNextAsync().AsTask();
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.RevokeAsync(policy, 1, [TargetRevocation()]);
            runtime.Gate.SetResult();
            await Assert.ThrowsAsync<ModelScopeException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            var pending = session.InvokeAsync(request);
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.RevokeAsync(policy, 1, [TargetRevocation()]);
            runtime.Gate.SetResult();
            await Assert.ThrowsAsync<ModelScopeException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    private static CertificationRevocation TargetRevocation() =>
        new("tests/model", new string('a', 40), "raw-onnx", "onnx", Digest);

    private sealed class RevocationClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }
    [Fact]
    public async Task ProductionRejectsRuntimeSelfCertificationWithoutSignedPolicy()
    {
        var runtime = new RecordingRuntime("onnx");
        var router = new RuntimeRouter([runtime], new() { Mode = RuntimePolicyMode.Production });
        Assert.Equal(CapabilityStatus.Compatible, Assert.Single(router.Evaluate(Capabilities("/tmp"))).Status);
        await Assert.ThrowsAsync<ModelScopeException>(() => router.CreateSessionAsync(Capabilities("/tmp")));
        Assert.Equal(0, runtime.Loads);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("task")]
    [InlineData("model")]
    [InlineData("platform")]
    [InlineData("digest")]
    [InlineData("runtime")]
    [InlineData("branch")]
    public async Task SignedAuthorizationRejectsAnyIdentityMismatch(string mismatch)
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync(
            platform: mismatch == "platform" ? "unapproved" : "test-cpu",
            digest: mismatch == "digest" ? "sha256:" + new string('b', 64) : Digest);
        var runtime = new RecordingRuntime(mismatch == "runtime" ? "python" : "onnx");
        var router = new RuntimeRouter([runtime], new() { Mode = RuntimePolicyMode.Production, ProductionPolicy = policy });
        var cap = Capabilities(fixture.Directory);
        cap = mismatch switch
        {
            "revision" => cap with { Revision = new string('b', 40) },
            "task" => cap with { Task = "text-generation" },
            "model" => cap with { ModelId = "private/different" },
            "branch" => cap with { Revision = "master" },
            _ => cap,
        };
        await Assert.ThrowsAsync<ModelScopeException>(() => router.CreateSessionAsync(cap));
        Assert.Equal(0, runtime.Loads);
    }

    [Fact]
    public async Task ExactPolicyAuthorizesCompatibleRuntimeAndFreezesCallerConfiguration()
    {
        using var fixture = new Fixture();
        var digests = new Dictionary<string, string> { ["onnx"] = Digest };
        var policy = await fixture.PolicyAsync(digests: digests);
        digests["onnx"] = "sha256:" + new string('c', 64);
        var runtime = new RecordingRuntime("onnx", CapabilityStatus.Compatible);
        var options = new RuntimeRouterOptions { Mode = RuntimePolicyMode.Production, ProductionPolicy = policy };
        var router = new RuntimeRouter([runtime], options);
        options.Mode = RuntimePolicyMode.Development;
        options.ProductionPolicy = null;
        Assert.Equal(CapabilityStatus.Certified, Assert.Single(router.Evaluate(Capabilities(fixture.Directory))).Status);
        await using var session = await router.CreateSessionAsync(Capabilities(fixture.Directory));
        Assert.Equal(1, runtime.Loads);
        await Assert.ThrowsAsync<ModelScopeException>(() => router.CreateSessionAsync(
            Capabilities(fixture.Directory) with { Revision = new string('f', 40) }));
        Assert.Equal(1, runtime.Loads);
    }

    [Theory]
    [InlineData("modified")]
    [InlineData("missing")]
    [InlineData("unlisted")]
    public async Task IntegrityFailurePreventsLoadAndDoesNotFallBack(string problem)
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        var runtime = new RecordingRuntime("onnx");
        var remote = new CloudRuntime();
        var router = new RuntimeRouter([runtime, remote], new()
        {
            Mode = RuntimePolicyMode.Production,
            ProductionPolicy = policy,
            AllowRemoteFallback = true
        });
        var cap = Capabilities(fixture.Directory);
        if (problem == "modified") await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "model.onnx"), "changed");
        if (problem == "missing") File.Delete(Path.Combine(fixture.Directory, "model.onnx"));
        if (problem == "unlisted") cap = cap with { Artifacts = [.. cap.Artifacts, new("extra.bin", ModelArtifactFormat.PyTorch, 1)] };
        var error = await Assert.ThrowsAsync<ModelScopeException>(() => router.CreateSessionAsync(cap));
        Assert.Equal(ModelScopeErrorCode.DownloadIntegrityFailed, error.Code);
        Assert.Equal(0, runtime.Loads);
        Assert.Equal(0, remote.Loads);
        Assert.DoesNotContain(fixture.Directory, error.Message);
    }

    [Fact]
    public async Task PolicyRejectsTamperedSignatureAndUncertifiedEntry()
    {
        using var fixture = new Fixture();
        await fixture.PolicyAsync();
        await File.AppendAllTextAsync(fixture.CatalogPath, " ");
        await Assert.ThrowsAsync<ModelScopeException>(() => ProductionRuntimePolicy.LoadAsync(
            fixture.CatalogPath, fixture.SignaturePath, fixture.PublicKey, "test-cpu",
            new Dictionary<string, string> { ["onnx"] = Digest }));
        var policy = await fixture.PolicyAsync(status: CapabilityStatus.Compatible);
        var runtime = new RecordingRuntime("onnx");
        await Assert.ThrowsAsync<ModelScopeException>(() => new RuntimeRouter([runtime],
            new() { Mode = RuntimePolicyMode.Production, ProductionPolicy = policy }).CreateSessionAsync(Capabilities(fixture.Directory)));
        Assert.Equal(0, runtime.Loads);
    }

    [Fact]
    public async Task RemoteEgressRequiresExplicitSelectionOrOptIn_EvenWithOnlyRemoteInstalled()
    {
        var remote = new CloudRuntime();
        var router = new RuntimeRouter([remote]);
        await Assert.ThrowsAsync<ModelScopeException>(() => router.CreateSessionAsync(Capabilities("/tmp")));
        Assert.Equal(0, remote.Loads);
        await using var selected = await router.CreateSessionAsync(Capabilities("/tmp"), "cloud");
        Assert.Equal(1, remote.Loads);
        await using var optedIn = await new RuntimeRouter([remote], new() { AllowRemoteFallback = true })
            .CreateSessionAsync(Capabilities("/tmp"));
        Assert.Equal(2, remote.Loads);
    }

    [Fact]
    public async Task DevelopmentTriesLocalBeforeRemoteAndNeverLeaksAfterFailureWithoutOptIn()
    {
        var local = new RecordingRuntime("python", fail: true);
        var remote = new CloudRuntime();
        var router = new RuntimeRouter([remote, local]);
        Assert.Equal("python", router.Evaluate(Capabilities("/tmp"))[0].RuntimeName);
        await Assert.ThrowsAsync<ModelScopeException>(() => router.CreateSessionAsync(Capabilities("/tmp")));
        Assert.Equal(1, local.Loads);
        Assert.Equal(0, remote.Loads);
        await using var session = await new RuntimeRouter([remote, local], new() { AllowRemoteFallback = true })
            .CreateSessionAsync(Capabilities("/tmp"));
        Assert.Equal(2, local.Loads);
        Assert.Equal(1, remote.Loads);
    }

    [Fact]
    public async Task ExplicitLocalNeverFallsBackToRemote()
    {
        var local = new RecordingRuntime("python", fail: true);
        var remote = new CloudRuntime();
        await Assert.ThrowsAsync<ModelScopeException>(() =>
            new RuntimeRouter([local, remote], new() { AllowRemoteFallback = true })
                .CreateSessionAsync(Capabilities("/tmp"), "python"));
        Assert.Equal(0, remote.Loads);
    }

    private const string Digest = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    [Fact]
    public async Task CertifiedSessionCannotInvokeAnUncertifiedTask()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync();
        var router = new RuntimeRouter([new RecordingRuntime("onnx")],
            new() { Mode = RuntimePolicyMode.Production, ProductionPolicy = policy });
        await using var session = await router.CreateSessionAsync(Capabilities(fixture.Directory));
        var bad = new ModelRequest("text-generation", JsonSerializer.SerializeToElement("input"));
        await Assert.ThrowsAsync<ModelScopeException>(() => session.InvokeAsync(bad));
        Assert.Throws<ModelScopeException>(() => session.InvokeStreamingAsync(bad));
        Assert.Equal("ok", (await session.InvokeAsync(bad with { Task = "raw-onnx" })).Output.GetString());
    }

    [Fact]
    public async Task ProductionRemoteFallbackRequiresSeparateCertificationAndExplicitOptIn()
    {
        using var fixture = new Fixture();
        var policy = await fixture.PolicyAsync(includeCloud: true);
        var local = new RecordingRuntime("onnx", fail: true);
        var remote = new CloudRuntime();
        var options = new RuntimeRouterOptions { Mode = RuntimePolicyMode.Production, ProductionPolicy = policy };
        await Assert.ThrowsAsync<ModelScopeException>(() => new RuntimeRouter([local, remote], options)
            .CreateSessionAsync(Capabilities(fixture.Directory)));
        Assert.Equal(0, remote.Loads);
        options.AllowRemoteFallback = true;
        await using var session = await new RuntimeRouter([local, remote], options).CreateSessionAsync(Capabilities(fixture.Directory));
        Assert.Equal(1, remote.Loads);
    }

    private static ModelCapabilities Capabilities(string path) => new(path, "tests/model", new string('a', 40),
        "raw-onnx", [], [new("model.onnx", ModelArtifactFormat.Onnx, 7)], [], false, []);

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"production-policy-{Guid.NewGuid():N}");
        private readonly RSA _rsa = RSA.Create(2048);
        public string CatalogPath => Path.Combine(Directory, "catalog.json");
        public string SignaturePath => Path.Combine(Directory, "catalog.sig");
        public string PublicKey => _rsa.ExportSubjectPublicKeyInfoPem();

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, "model.onnx"), "fixture");
        }

        public async Task<ProductionRuntimePolicy> PolicyAsync(string platform = "test-cpu", string digest = Digest,
            CapabilityStatus status = CapabilityStatus.Certified, Dictionary<string, string>? digests = null,
            bool includeCloud = false, TimeProvider? clock = null, bool initializeRevocations = true)
        {
            var entry = new CompatibilityCatalogEntry("tests/model", new string('a', 40), "raw-onnx", "onnx", status,
                new Dictionary<string, string>
                {
                    ["model.onnx"] = Convert.ToHexStringLower(SHA256.HashData("fixture"u8))
                }, ["test-cpu"], Digest);
            CompatibilityCatalogEntry[] entries = includeCloud ? [entry, entry with { Runtime = "cloud" }] : [entry];
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new CompatibilityCatalog(1, "policy-test", DateTimeOffset.UtcNow, entries),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllBytesAsync(CatalogPath, bytes);
            await File.WriteAllTextAsync(SignaturePath,
                Convert.ToBase64String(_rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
            digests ??= new Dictionary<string, string> { ["onnx"] = digest };
            if (includeCloud) digests["cloud"] = Digest;
            var policy = await ProductionRuntimePolicy.LoadAsync(CatalogPath, SignaturePath, PublicKey, platform, digests, timeProvider: clock);
            if (initializeRevocations) await RevokeAsync(policy, 0, [], time: clock?.GetUtcNow());
            return policy;
        }

        public async Task RevokeAsync(ProductionRuntimePolicy policy, long sequence, CertificationRevocation[] revoked,
            string scope = "policy-test", bool tamper = false, TimeSpan? offset = null, DateTimeOffset? time = null,
            bool prepareOnly = false)
        {
            var now = (time ?? DateTimeOffset.UtcNow) + (offset ?? TimeSpan.Zero);
            // Reserve wire sequence 1 for the fixture's initial fresh empty list.
            var list = new CertificationRevocationList(1, scope, sequence + 1, now, now.AddHours(1), revoked);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(list, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var path = Path.Combine(Directory, "revocations.json");
            var signature = Path.Combine(Directory, "revocations.sig");
            await File.WriteAllBytesAsync(path, bytes);
            await File.WriteAllTextAsync(signature,
                Convert.ToBase64String(_rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
            if (tamper) await File.AppendAllTextAsync(path, " ");
            if (!prepareOnly) await policy.ApplyRevocationsAsync(path, signature);
        }

        public Task<ProductionRuntimePolicy> RestoreAsync(string path, RevocationCheckpointAnchor anchor, TimeProvider? clock = null) =>
            ProductionRuntimePolicy.LoadWithRevocationCheckpointAsync(CatalogPath, SignaturePath, PublicKey, "test-cpu",
                new Dictionary<string, string> { ["onnx"] = Digest }, path, anchor, timeProvider: clock);

        public void Dispose()
        {
            _rsa.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class BlockingPublicationStore : IRevocationPublicationStore
    {
        private RevocationPublicationStoreState _state = new(null, null);
        public TaskCompletionSource StageEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RevocationPublicationStoreState> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_state);

        public async Task StageAsync(PendingRevocationPublication pending, CancellationToken cancellationToken = default)
        {
            StageEntered.SetResult();
            await ReleaseStage.Task.WaitAsync(cancellationToken);
            _state = new(_state.Committed, pending);
        }

        public Task CommitAsync(PendingRevocationPublication pending, CommittedRevocationPublication committed,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(_state.Pending, pending);
            _state = new(committed, null);
            return Task.CompletedTask;
        }
    }

    private sealed class CommitThenThrowPublicationStore(IRevocationPublicationStore inner)
        : IRevocationPublicationStore
    {
        private bool _throw = true;

        public Task<RevocationPublicationStoreState> ReadAsync(CancellationToken cancellationToken = default) =>
            inner.ReadAsync(cancellationToken);

        public Task StageAsync(PendingRevocationPublication pending, CancellationToken cancellationToken = default) =>
            inner.StageAsync(pending, cancellationToken);

        public async Task CommitAsync(PendingRevocationPublication pending, CommittedRevocationPublication committed,
            CancellationToken cancellationToken = default)
        {
            await inner.CommitAsync(pending, committed, cancellationToken);
            if (_throw)
            {
                _throw = false;
                throw new IOException("Simulated lost commit acknowledgement.");
            }
        }
    }

    private class RecordingRuntime(string name, CapabilityStatus status = CapabilityStatus.Certified, bool fail = false) : IModelRuntime
    {
        public string Name => name;
        public int Loads { get; private set; }
        public TaskCompletionSource? Gate { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RuntimeCandidate Evaluate(ModelCapabilities capabilities) => new(Name, status, "fixture", 1);
        public Task<IModelSession> CreateSessionAsync(ModelCapabilities capabilities, CancellationToken cancellationToken = default)
        {
            Loads++;
            if (fail) throw new ModelScopeException(ModelScopeErrorCode.ModelLoadFailed, "fixture", isRetryable: true);
            return Task.FromResult<IModelSession>(new Session(capabilities, this));
        }
    }

    private sealed class CloudRuntime() : RecordingRuntime("cloud"), IRemoteModelRuntime;

    private sealed class Session(ModelCapabilities capabilities, RecordingRuntime runtime) : IModelSession
    {
        public ModelCapabilities Capabilities => capabilities;
        public async Task<ModelResponse> InvokeAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            runtime.Started.TrySetResult();
            if (runtime.Gate is not null) await runtime.Gate.Task.WaitAsync(cancellationToken);
            return new ModelResponse(JsonSerializer.SerializeToElement("ok"), capabilities.ModelId!,
                capabilities.Revision!, "fixture", TimeSpan.Zero);
        }
        public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (runtime.Gate is not null)
            {
                yield return new("chunk", JsonSerializer.SerializeToElement("first"), false);
                runtime.Started.TrySetResult();
                await runtime.Gate.Task.WaitAsync(cancellationToken);
            }
            yield return new("done", JsonSerializer.SerializeToElement("ok"), true);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
