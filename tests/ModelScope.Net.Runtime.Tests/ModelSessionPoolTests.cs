using System.Collections.Concurrent;
using System.Text.Json;
using ModelScope.Net.Runtime;

namespace ModelScope.Net.Runtime.Tests;

public sealed class ModelSessionPoolTests
{
    [Fact]
    public async Task CacheIdentitySeparatesTasksAndCannotBypassRemoteCodePolicy()
    {
        var runtime = new PoolTestRuntime();
        await using var pool = CreatePool(runtime);
        var first = CreateRequest("owner/a", 20);
        await using var original = await pool.AcquireAsync(first);
        var second = first with { Capabilities = first.Capabilities with { Task = "different-task" } };
        await using var otherTask = await pool.AcquireAsync(second);
        Assert.NotSame(original.Session, otherTask.Session);
        Assert.Equal("different-task", otherTask.Session.Capabilities.Task);
        var executable = first with { Capabilities = first.Capabilities with { ContainsRemoteCode = true } };
        var error = await Assert.ThrowsAsync<ModelScopeException>(() => pool.AcquireAsync(executable));
        Assert.Equal(ModelScopeErrorCode.RemoteCodeNotAllowed, error.Code);
        Assert.Equal(2, runtime.CreateCalls["owner/a"]);
    }

    [Fact]
    public async Task PrewarmAndAcquire_ReuseSingleLoadedSession()
    {
        var runtime = new PoolTestRuntime();
        var request = CreateRequest("owner/a", 40);
        await using var pool = CreatePool(runtime);

        var warmed = await pool.PrewarmAsync([request]);
        await using var first = await pool.AcquireAsync(request);
        await first.DisposeAsync();
        await using var second = await pool.AcquireAsync(request);

        Assert.True(Assert.Single(warmed).Created);
        Assert.Same(first.Session, second.Session);
        Assert.Equal(1, runtime.CreateCalls["owner/a"]);
        Assert.Equal(1, pool.GetSnapshot().CachedSessions);
    }

    [Fact]
    public async Task LruEviction_RemovesLeastRecentlyUsedInactiveSession()
    {
        var runtime = new PoolTestRuntime();
        var time = new ManualTimeProvider();
        await using var pool = CreatePool(runtime, new ModelSessionPoolOptions
        {
            MaxCachedSessions = 2,
            MaxEstimatedMemoryBytes = 100,
            MaxConcurrentLeases = 2,
            MaxConcurrentLeasesPerModel = 1,
        }, time);
        var a = CreateRequest("owner/a", 40);
        var b = CreateRequest("owner/b", 40);
        var c = CreateRequest("owner/c", 40);

        await pool.PrewarmAsync([a]);
        time.Advance(TimeSpan.FromSeconds(1));
        await pool.PrewarmAsync([b]);
        time.Advance(TimeSpan.FromSeconds(1));
        await using (var lease = await pool.AcquireAsync(a))
        {
        }
        time.Advance(TimeSpan.FromSeconds(1));
        await pool.PrewarmAsync([c]);

        Assert.False(runtime.Session("owner/a").IsDisposed);
        Assert.True(runtime.Session("owner/b").IsDisposed);
        Assert.False(runtime.Session("owner/c").IsDisposed);
        Assert.Equal(2, pool.GetSnapshot().CachedSessions);
    }

    [Fact]
    public async Task ActiveLease_PreventsEvictionUntilReleased()
    {
        var runtime = new PoolTestRuntime();
        await using var pool = CreatePool(runtime, new ModelSessionPoolOptions
        {
            MaxCachedSessions = 1,
            MaxEstimatedMemoryBytes = 100,
            MaxConcurrentLeases = 1,
            MaxConcurrentLeasesPerModel = 1,
        });
        var a = CreateRequest("owner/a", 40);
        var b = CreateRequest("owner/b", 40);
        var lease = await pool.AcquireAsync(a);

        var blocked = await Assert.ThrowsAsync<ModelScopeException>(() => pool.PrewarmAsync([b]));

        Assert.Equal(ModelScopeErrorCode.ResourceQuotaExceeded, blocked.Code);
        Assert.False(runtime.Session("owner/a").IsDisposed);
        await lease.DisposeAsync();
        await pool.PrewarmAsync([b]);
        Assert.True(runtime.Session("owner/a").IsDisposed);
    }

    [Fact]
    public async Task BoundedQueue_RejectsOverflowAndDispatchesAfterRelease()
    {
        var runtime = new PoolTestRuntime();
        await using var pool = CreatePool(runtime, new ModelSessionPoolOptions
        {
            MaxCachedSessions = 2,
            MaxEstimatedMemoryBytes = 100,
            MaxConcurrentLeases = 1,
            MaxConcurrentLeasesPerModel = 1,
            MaxQueuedAcquisitions = 1,
            QueueTimeout = TimeSpan.FromSeconds(5),
        });
        var request = CreateRequest("owner/a", 40);
        var first = await pool.AcquireAsync(request);
        var queued = pool.AcquireAsync(request);
        await WaitUntilAsync(() => pool.GetSnapshot().QueuedAcquisitions == 1);

        var overflow = await Assert.ThrowsAsync<ModelScopeException>(() => pool.AcquireAsync(request));

        Assert.Equal(ModelScopeErrorCode.ResourceQuotaExceeded, overflow.Code);
        await first.DisposeAsync();
        await using var granted = await queued;
        Assert.Equal(1, pool.GetSnapshot().ActiveLeases);
        await granted.DisposeAsync();
        Assert.Equal(0, pool.GetSnapshot().ActiveLeases);
    }

    [Fact]
    public async Task QueueTimeout_ReturnsRetryableQuotaErrorWithoutLeakingCapacity()
    {
        var runtime = new PoolTestRuntime();
        await using var pool = CreatePool(runtime, new ModelSessionPoolOptions
        {
            MaxCachedSessions = 1,
            MaxEstimatedMemoryBytes = 100,
            MaxConcurrentLeases = 1,
            MaxConcurrentLeasesPerModel = 1,
            MaxQueuedAcquisitions = 1,
            QueueTimeout = TimeSpan.FromMilliseconds(50),
        });
        var request = CreateRequest("owner/a", 40);
        await using var held = await pool.AcquireAsync(request);

        var timeout = await Assert.ThrowsAsync<ModelScopeException>(() => pool.AcquireAsync(request));

        Assert.Equal(ModelScopeErrorCode.ResourceQuotaExceeded, timeout.Code);
        Assert.True(timeout.IsRetryable);
        Assert.Equal(1, pool.GetSnapshot().ActiveLeases);
        Assert.Equal(0, pool.GetSnapshot().QueuedAcquisitions);
    }

    [Fact]
    public async Task PerModelLimit_QueuesSameModelButAllowsAnotherModel()
    {
        var runtime = new PoolTestRuntime();
        await using var pool = CreatePool(runtime, new ModelSessionPoolOptions
        {
            MaxCachedSessions = 2,
            MaxEstimatedMemoryBytes = 100,
            MaxConcurrentLeases = 2,
            MaxConcurrentLeasesPerModel = 1,
            MaxQueuedAcquisitions = 1,
            QueueTimeout = TimeSpan.FromSeconds(5),
        });
        var a = CreateRequest("owner/a", 40);
        var b = CreateRequest("owner/b", 40);
        var firstA = await pool.AcquireAsync(a);

        await using var firstB = await pool.AcquireAsync(b);
        var secondA = pool.AcquireAsync(a);
        await WaitUntilAsync(() => pool.GetSnapshot().QueuedAcquisitions == 1);
        await firstB.DisposeAsync();

        Assert.False(secondA.IsCompleted);
        await firstA.DisposeAsync();
        await using var grantedA = await secondA;
        Assert.Equal(1, pool.GetSnapshot().ActiveLeases);
    }

    [Fact]
    public async Task PendingAcquisition_PreventsEvictionBeforeLeaseIsGranted()
    {
        var runtime = new PoolTestRuntime();
        await using var pool = CreatePool(runtime, new ModelSessionPoolOptions
        {
            MaxCachedSessions = 2,
            MaxEstimatedMemoryBytes = 100,
            MaxConcurrentLeases = 1,
            MaxConcurrentLeasesPerModel = 1,
            MaxQueuedAcquisitions = 1,
            QueueTimeout = TimeSpan.FromSeconds(5),
        });
        var a = CreateRequest("owner/a", 30);
        var b = CreateRequest("owner/b", 30);
        var c = CreateRequest("owner/c", 30);
        var heldB = await pool.AcquireAsync(b);
        var pendingA = pool.AcquireAsync(a);
        await WaitUntilAsync(() => pool.GetSnapshot().QueuedAcquisitions == 1);

        var blocked = await Assert.ThrowsAsync<ModelScopeException>(() => pool.PrewarmAsync([c]));

        Assert.Equal(ModelScopeErrorCode.ResourceQuotaExceeded, blocked.Code);
        Assert.False(runtime.Session("owner/a").IsDisposed);
        await heldB.DisposeAsync();
        await using var grantedA = await pendingA;
        Assert.Same(runtime.Session("owner/a"), grantedA.Session);
    }

    [Fact]
    public async Task MemoryQuota_EvictsInactiveSessionAndRejectsOversizedModel()
    {
        var runtime = new PoolTestRuntime();
        await using var pool = CreatePool(runtime, new ModelSessionPoolOptions
        {
            MaxCachedSessions = 3,
            MaxEstimatedMemoryBytes = 100,
            MaxConcurrentLeases = 1,
            MaxConcurrentLeasesPerModel = 1,
        });

        await pool.PrewarmAsync([CreateRequest("owner/a", 60)]);
        await pool.PrewarmAsync([CreateRequest("owner/b", 50)]);

        Assert.True(runtime.Session("owner/a").IsDisposed);
        Assert.Equal(50, pool.GetSnapshot().EstimatedMemoryBytes);
        var oversized = await Assert.ThrowsAsync<ModelScopeException>(
            () => pool.PrewarmAsync([CreateRequest("owner/c", 101)]));
        Assert.Equal(ModelScopeErrorCode.ResourceQuotaExceeded, oversized.Code);
    }

    [Fact]
    public async Task Trim_RemovesOnlyIdleInactiveSessions()
    {
        var runtime = new PoolTestRuntime();
        var time = new ManualTimeProvider();
        await using var pool = CreatePool(runtime, new ModelSessionPoolOptions
        {
            MaxCachedSessions = 2,
            MaxEstimatedMemoryBytes = 100,
            MaxConcurrentLeases = 1,
            MaxConcurrentLeasesPerModel = 1,
            IdleTimeout = TimeSpan.FromMinutes(5),
        }, time);
        var request = CreateRequest("owner/a", 40);
        await pool.PrewarmAsync([request]);
        time.Advance(TimeSpan.FromMinutes(6));

        var removed = await pool.TrimAsync();

        Assert.Equal(1, removed);
        Assert.True(runtime.Session("owner/a").IsDisposed);
        Assert.Empty(pool.GetSnapshot().Entries);
    }

    [Fact]
    public async Task FailedLoad_IsRemovedSoNextAcquireCanRetry()
    {
        var runtime = new PoolTestRuntime(failFirstModel: "owner/a");
        await using var pool = CreatePool(runtime);
        var request = CreateRequest("owner/a", 40);

        await Assert.ThrowsAsync<ModelScopeException>(() => pool.AcquireAsync(request));
        await using var recovered = await pool.AcquireAsync(request);

        Assert.Equal(2, runtime.CreateCalls["owner/a"]);
        Assert.False(((PoolTestSession)recovered.Session).IsDisposed);
    }

    [Fact]
    public async Task SharedFailedLoad_FailsAllAcquirersWithoutLeavingAWaiter()
    {
        var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new PoolTestRuntime(failFirstModel: "owner/a", loadGate: loadGate.Task);
        await using var pool = CreatePool(runtime);
        var request = CreateRequest("owner/a", 40);
        var first = pool.AcquireAsync(request);
        await WaitUntilAsync(() => runtime.CreateCalls.ContainsKey("owner/a"));
        var second = pool.AcquireAsync(request);

        loadGate.SetResult();
        var firstError = await Assert.ThrowsAsync<ModelScopeException>(() => first);
        var secondError = await Assert.ThrowsAsync<ModelScopeException>(() => second);

        Assert.Equal(ModelScopeErrorCode.ModelLoadFailed, firstError.Code);
        Assert.Equal(ModelScopeErrorCode.ModelLoadFailed, secondError.Code);
        Assert.Equal(0, pool.GetSnapshot().QueuedAcquisitions);
        Assert.Empty(pool.GetSnapshot().Entries);
    }

    [Fact]
    public async Task Snapshot_UsesFingerprintInsteadOfModelIdentityOrPath()
    {
        var runtime = new PoolTestRuntime();
        await using var pool = CreatePool(runtime);
        var request = CreateRequest("private/secret-model", 40);
        await pool.PrewarmAsync([request]);

        var json = JsonSerializer.Serialize(pool.GetSnapshot());

        Assert.DoesNotContain("private/secret-model", json, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Capabilities.ModelPath, json, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{16}$", Assert.Single(pool.GetSnapshot().Entries).ModelFingerprint);
    }

    private static ModelSessionPool CreatePool(
        PoolTestRuntime runtime,
        ModelSessionPoolOptions? options = null,
        TimeProvider? timeProvider = null) =>
        new(new RuntimeRouter([runtime]), options ?? new ModelSessionPoolOptions
        {
            MaxCachedSessions = 3,
            MaxEstimatedMemoryBytes = 100,
            MaxConcurrentLeases = 2,
            MaxConcurrentLeasesPerModel = 1,
            MaxQueuedAcquisitions = 4,
        }, timeProvider);

    private static ModelSessionPoolRequest CreateRequest(string modelId, long estimatedBytes)
    {
        var safeName = modelId.Replace('/', '-');
        var capabilities = new ModelCapabilities(
            $"/models/{safeName}",
            modelId,
            "0123456789abcdef0123456789abcdef01234567",
            "text-generation",
            [],
            [],
            [new RuntimeCandidate("test", CapabilityStatus.Certified, "test", 100)],
            ContainsRemoteCode: false,
            []);
        return new ModelSessionPoolRequest(capabilities, estimatedBytes, "test");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class PoolTestRuntime(string? failFirstModel = null, Task? loadGate = null) : IModelRuntime
    {
        private readonly ConcurrentDictionary<string, PoolTestSession> _sessions = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, int> CreateCalls { get; } = new(StringComparer.Ordinal);

        public string Name => "test";

        public RuntimeCandidate Evaluate(ModelCapabilities capabilities) =>
            new(Name, CapabilityStatus.Certified, "test", 100);

        public async Task<IModelSession> CreateSessionAsync(
            ModelCapabilities capabilities,
            CancellationToken cancellationToken = default)
        {
            var modelId = capabilities.ModelId!;
            var calls = CreateCalls.AddOrUpdate(modelId, 1, (_, current) => current + 1);
            if (loadGate is not null) await loadGate.WaitAsync(cancellationToken);
            if (modelId == failFirstModel && calls == 1)
            {
                throw new ModelScopeException(ModelScopeErrorCode.ModelLoadFailed, "planned failure");
            }

            var session = new PoolTestSession(capabilities);
            _sessions[modelId] = session;
            return session;
        }

        public PoolTestSession Session(string modelId) => _sessions[modelId];
    }

    private sealed class PoolTestSession(ModelCapabilities capabilities) : IModelSession
    {
        public ModelCapabilities Capabilities { get; } = capabilities;

        public bool IsDisposed { get; private set; }

        public Task<ModelResponse> InvokeAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelResponse(
                JsonSerializer.SerializeToElement(new { ok = true }),
                Capabilities.ModelId!,
                Capabilities.Revision!,
                "test",
                TimeSpan.Zero));

        public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ModelStreamEvent("done", JsonSerializer.SerializeToElement(new { ok = true }), true);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.Parse("2026-09-02T00:00:00Z");

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
