using System.Security.Cryptography;
using System.Text;

namespace ModelScope.Net.Runtime;

public sealed class ModelSessionPoolOptions
{
    public int MaxCachedSessions { get; set; } = 5;

    public long MaxEstimatedMemoryBytes { get; set; } = 3L * 1024 * 1024 * 1024;

    public int MaxConcurrentLeases { get; set; } = 1;

    public int MaxConcurrentLeasesPerModel { get; set; } = 1;

    public int MaxQueuedAcquisitions { get; set; } = 32;

    public TimeSpan QueueTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed record ModelSessionPoolRequest(
    ModelCapabilities Capabilities,
    long EstimatedMemoryBytes,
    string? PreferredRuntime = null);

public sealed record ModelSessionPrewarmResult(
    string ModelFingerprint,
    string Runtime,
    bool Created,
    long EstimatedMemoryBytes);

public sealed record ModelSessionPoolEntryStatus(
    string ModelFingerprint,
    string Runtime,
    long EstimatedMemoryBytes,
    int ActiveLeases,
    bool IsLoading,
    DateTimeOffset LastUsedAt);

public sealed record ModelSessionPoolSnapshot(
    int CachedSessions,
    long EstimatedMemoryBytes,
    int ActiveLeases,
    int QueuedAcquisitions,
    IReadOnlyList<ModelSessionPoolEntryStatus> Entries);

public sealed class ModelSessionLease : IAsyncDisposable
{
    private readonly Action _release;
    private int _disposed;

    internal ModelSessionLease(IModelSession session, string modelFingerprint, Action release)
    {
        Session = session;
        ModelFingerprint = modelFingerprint;
        _release = release;
    }

    public IModelSession Session { get; }

    public string ModelFingerprint { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _release();
        return ValueTask.CompletedTask;
    }
}

public sealed class ModelSessionPool : IAsyncDisposable
{
    private readonly RuntimeRouter _router;
    private readonly ModelSessionPoolOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<LeaseWaiter> _waiters = new();
    private int _activeLeases;
    private bool _disposed;

    public ModelSessionPool(
        RuntimeRouter router,
        ModelSessionPoolOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _options = options ?? new ModelSessionPoolOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        ValidateOptions(_options);
    }

    public async Task<ModelSessionLease> AcquireAsync(
        ModelSessionPoolRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var (entry, _) = await EnsureEntryAsync(
            request,
            reserveAcquisition: true,
            cancellationToken).ConfigureAwait(false);
        IModelSession session;
        try
        {
            session = await entry.SessionTask.ConfigureAwait(false);
        }
        catch
        {
            CancelPendingAcquisition(entry);
            throw;
        }

        await WaitForCapacityAsync(entry, cancellationToken).ConfigureAwait(false);
        return new ModelSessionLease(session, entry.Fingerprint, () => Release(entry));
    }

    public async Task<IReadOnlyList<ModelSessionPrewarmResult>> PrewarmAsync(
        IEnumerable<ModelSessionPoolRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var results = new List<ModelSessionPrewarmResult>();
        foreach (var request in requests)
        {
            ValidateRequest(request);
            var (entry, created) = await EnsureEntryAsync(
                request,
                reserveAcquisition: false,
                cancellationToken).ConfigureAwait(false);
            await entry.SessionTask.ConfigureAwait(false);
            results.Add(new ModelSessionPrewarmResult(
                entry.Fingerprint,
                entry.Runtime,
                created,
                entry.EstimatedMemoryBytes));
        }

        return results;
    }

    public async Task<int> TrimAsync(CancellationToken cancellationToken = default)
    {
        List<Task<IModelSession>> evicted;
        lock (_sync)
        {
            ThrowIfDisposed();
            var cutoff = _timeProvider.GetUtcNow() - _options.IdleTimeout;
            var candidates = _entries.Values
                .Where(entry =>
                    entry.ActiveLeases == 0 &&
                    entry.PendingAcquisitions == 0 &&
                    entry.SessionTask.IsCompletedSuccessfully &&
                    entry.LastUsedAt <= cutoff)
                .OrderBy(entry => entry.LastUsedAt)
                .ToArray();
            evicted = candidates.Select(RemoveEntry).ToList();
        }

        foreach (var sessionTask in evicted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DisposeSessionAsync(sessionTask).ConfigureAwait(false);
        }

        return evicted.Count;
    }

    public ModelSessionPoolSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var entries = _entries.Values
                .OrderBy(entry => entry.LastUsedAt)
                .Select(entry => new ModelSessionPoolEntryStatus(
                    entry.Fingerprint,
                    entry.Runtime,
                    entry.EstimatedMemoryBytes,
                    entry.ActiveLeases,
                    !entry.SessionTask.IsCompleted,
                    entry.LastUsedAt))
                .ToArray();
            return new ModelSessionPoolSnapshot(
                entries.Length,
                entries.Sum(entry => entry.EstimatedMemoryBytes),
                _activeLeases,
                _waiters.Count,
                entries);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<IModelSession>[] sessions;
        LeaseWaiter[] waiters;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = _entries.Values.Select(entry => entry.SessionTask).ToArray();
            _entries.Clear();
            waiters = _waiters.ToArray();
            _waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.State = WaiterState.Cancelled;
            waiter.Completion.TrySetException(new ObjectDisposedException(nameof(ModelSessionPool)));
        }

        foreach (var session in sessions)
        {
            await DisposeSessionAsync(session).ConfigureAwait(false);
        }
    }

    private async Task<(CacheEntry Entry, bool Created)> EnsureEntryAsync(
        ModelSessionPoolRequest request,
        bool reserveAcquisition,
        CancellationToken cancellationToken)
    {
        var key = CreateKey(request);
        CacheEntry entry;
        TaskCompletionSource<IModelSession>? creation = null;
        List<Task<IModelSession>> evicted = [];
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_entries.TryGetValue(key, out entry!))
            {
                entry.LastUsedAt = _timeProvider.GetUtcNow();
                if (reserveAcquisition) entry.PendingAcquisitions++;
                return (entry, false);
            }

            EnsureCapacityFor(request.EstimatedMemoryBytes, evicted);
            creation = new TaskCompletionSource<IModelSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            entry = new CacheEntry(
                key,
                CreateFingerprint(request),
                NormalizeRuntime(request.PreferredRuntime),
                request.EstimatedMemoryBytes,
                creation.Task,
                _timeProvider.GetUtcNow());
            if (reserveAcquisition) entry.PendingAcquisitions++;
            _entries.Add(key, entry);
        }

        foreach (var session in evicted)
        {
            await DisposeSessionAsync(session).ConfigureAwait(false);
        }

        try
        {
            var loaded = await _router.CreateSessionAsync(
                request.Capabilities,
                request.PreferredRuntime,
                cancellationToken).ConfigureAwait(false);
            creation!.SetResult(loaded);
            return (entry, true);
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(key);
                }
            }

            creation!.SetException(exception);
            _ = creation.Task.Exception;
            throw;
        }
    }

    private async Task WaitForCapacityAsync(CacheEntry entry, CancellationToken cancellationToken)
    {
        LeaseWaiter? waiter = null;
        lock (_sync)
        {
            try
            {
                ThrowIfDisposed();
            }
            catch
            {
                entry.PendingAcquisitions--;
                throw;
            }

            if (CanGrant(entry))
            {
                Grant(entry);
                return;
            }

            if (_waiters.Count >= _options.MaxQueuedAcquisitions)
            {
                entry.PendingAcquisitions--;
                throw QuotaExceeded("The model session acquisition queue is full.");
            }

            waiter = new LeaseWaiter(entry);
            waiter.Node = _waiters.AddLast(waiter);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.QueueTimeout);
        try
        {
            await waiter.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CancelWaiter(waiter);
            throw QuotaExceeded($"Model session acquisition exceeded the queue timeout of {_options.QueueTimeout}.");
        }
        catch (OperationCanceledException)
        {
            CancelWaiter(waiter);
            throw;
        }
    }

    private void CancelWaiter(LeaseWaiter waiter)
    {
        lock (_sync)
        {
            if (waiter.State == WaiterState.Queued)
            {
                if (waiter.Node?.List is not null) _waiters.Remove(waiter.Node);
                waiter.State = WaiterState.Cancelled;
                waiter.Entry.PendingAcquisitions--;
                return;
            }

            if (waiter.State == WaiterState.Granted)
            {
                waiter.State = WaiterState.Cancelled;
                waiter.Entry.ActiveLeases--;
                _activeLeases--;
                DispatchWaiters();
            }
        }
    }

    private void CancelPendingAcquisition(CacheEntry entry)
    {
        lock (_sync)
        {
            if (entry.PendingAcquisitions > 0) entry.PendingAcquisitions--;
        }
    }

    private void Release(CacheEntry entry)
    {
        lock (_sync)
        {
            if (entry.ActiveLeases <= 0 || _activeLeases <= 0) return;
            entry.ActiveLeases--;
            _activeLeases--;
            entry.LastUsedAt = _timeProvider.GetUtcNow();
            DispatchWaiters();
        }
    }

    private void DispatchWaiters()
    {
        var node = _waiters.First;
        while (node is not null && _activeLeases < _options.MaxConcurrentLeases)
        {
            var next = node.Next;
            var waiter = node.Value;
            if (waiter.State == WaiterState.Queued && CanGrant(waiter.Entry))
            {
                _waiters.Remove(node);
                waiter.Node = null;
                waiter.State = WaiterState.Granted;
                Grant(waiter.Entry);
                waiter.Completion.TrySetResult();
            }

            node = next;
        }
    }

    private bool CanGrant(CacheEntry entry) =>
        !_disposed &&
        _entries.TryGetValue(entry.Key, out var current) &&
        ReferenceEquals(current, entry) &&
        _activeLeases < _options.MaxConcurrentLeases &&
        entry.ActiveLeases < _options.MaxConcurrentLeasesPerModel;

    private void Grant(CacheEntry entry)
    {
        entry.PendingAcquisitions--;
        entry.ActiveLeases++;
        _activeLeases++;
        entry.LastUsedAt = _timeProvider.GetUtcNow();
    }

    private void EnsureCapacityFor(long estimatedMemoryBytes, List<Task<IModelSession>> evicted)
    {
        var totalMemory = _entries.Values.Sum(entry => entry.EstimatedMemoryBytes);
        while (_entries.Count >= _options.MaxCachedSessions ||
               totalMemory + estimatedMemoryBytes > _options.MaxEstimatedMemoryBytes)
        {
            var candidate = _entries.Values
                .Where(entry =>
                    entry.ActiveLeases == 0 &&
                    entry.PendingAcquisitions == 0 &&
                    entry.SessionTask.IsCompletedSuccessfully)
                .OrderBy(entry => entry.LastUsedAt)
                .FirstOrDefault();
            if (candidate is null)
            {
                throw QuotaExceeded("No inactive model session can be evicted to satisfy the cache quota.");
            }

            totalMemory -= candidate.EstimatedMemoryBytes;
            evicted.Add(RemoveEntry(candidate));
        }
    }

    private Task<IModelSession> RemoveEntry(CacheEntry entry)
    {
        _entries.Remove(entry.Key);
        return entry.SessionTask;
    }

    private static async Task DisposeSessionAsync(Task<IModelSession> sessionTask)
    {
        try
        {
            var session = await sessionTask.ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Failed loads have no usable session; eviction and shutdown remain best effort.
        }
    }

    private static void ValidateOptions(ModelSessionPoolOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxCachedSessions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxEstimatedMemoryBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentLeases, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentLeasesPerModel, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxQueuedAcquisitions);
        if (options.QueueTimeout <= TimeSpan.Zero || options.IdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Queue and idle timeouts must be positive.");
        }
    }

    private void ValidateRequest(ModelSessionPoolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Capabilities);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.EstimatedMemoryBytes, 1);
        if (request.EstimatedMemoryBytes > _options.MaxEstimatedMemoryBytes)
        {
            throw QuotaExceeded("The model estimate exceeds the complete session-pool memory quota.");
        }
    }

    private static string CreateKey(ModelSessionPoolRequest request)
    {
        var identity = string.Join('\n',
            request.Capabilities.ModelId ?? string.Empty,
            request.Capabilities.Revision ?? string.Empty,
            request.Capabilities.Task ?? string.Empty,
            request.Capabilities.ContainsRemoteCode ? "remote-code" : "no-remote-code",
            request.Capabilities.ModelPath,
            request.PreferredRuntime ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string CreateFingerprint(ModelSessionPoolRequest request) => CreateKey(request)[..16];

    private static string NormalizeRuntime(string? runtime) =>
        string.IsNullOrWhiteSpace(runtime) ? "auto" : runtime.Trim().ToLowerInvariant();

    private static ModelScopeException QuotaExceeded(string message) =>
        new(ModelScopeErrorCode.ResourceQuotaExceeded, message, isRetryable: true);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class CacheEntry(
        string key,
        string fingerprint,
        string runtime,
        long estimatedMemoryBytes,
        Task<IModelSession> sessionTask,
        DateTimeOffset lastUsedAt)
    {
        public string Key { get; } = key;
        public string Fingerprint { get; } = fingerprint;
        public string Runtime { get; } = runtime;
        public long EstimatedMemoryBytes { get; } = estimatedMemoryBytes;
        public Task<IModelSession> SessionTask { get; } = sessionTask;
        public int PendingAcquisitions { get; set; }
        public int ActiveLeases { get; set; }
        public DateTimeOffset LastUsedAt { get; set; } = lastUsedAt;
    }

    private sealed class LeaseWaiter(CacheEntry entry)
    {
        public CacheEntry Entry { get; } = entry;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<LeaseWaiter>? Node { get; set; }
        public WaiterState State { get; set; } = WaiterState.Queued;
    }

    private enum WaiterState
    {
        Queued,
        Granted,
        Cancelled,
    }
}
