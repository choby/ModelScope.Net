using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace ModelScope.Net.Runtime;

/// <summary>A verified Catalog/model binding. Runtime processes and images remain owned by the host.</summary>
public sealed class VerifiedModelRelease
{
    private readonly RuntimeRouter _router;
    private readonly ModelCapabilities _capabilities;

    private VerifiedModelRelease(string id, string catalogVersion, ModelCapabilities capabilities,
        string runtime, string runtimeDigest, RuntimeRouter router)
    {
        Id = id;
        CatalogVersion = catalogVersion;
        _capabilities = capabilities;
        Runtime = runtime;
        RuntimeDigest = runtimeDigest;
        _router = router;
    }

    public string Id { get; }
    public string CatalogVersion { get; }
    public string ModelId => _capabilities.ModelId!;
    public string Revision => _capabilities.Revision!;
    public string Task => _capabilities.Task!;
    public string Runtime { get; }
    public string RuntimeDigest { get; }

    public static async Task<VerifiedModelRelease> CreateAsync(string id, string catalogPath,
        string signaturePath, string publicKeyPem, ModelCapabilities capabilities,
        string runtime, string runtimeDigest, string platform, RuntimeRouter router,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Release ID must be a short opaque identifier.", nameof(id));
        if (runtimeDigest is null || runtimeDigest.Length != 71 ||
            !runtimeDigest.StartsWith("sha256:", StringComparison.Ordinal) || !runtimeDigest[7..].All(Uri.IsHexDigit))
            throw new ArgumentException("Pin the deployed runtime binary or image by SHA-256.", nameof(runtimeDigest));
        if (capabilities.Revision is null || capabilities.Revision.Length != 40 || !capabilities.Revision.All(Uri.IsHexDigit))
            throw new ArgumentException("A full fixed model Commit is required.", nameof(capabilities));

        var catalog = await CompatibilityCatalogLoader.LoadAndVerifyAsync(
            catalogPath, signaturePath, publicKeyPem, cancellationToken).ConfigureAwait(false);
        var entry = catalog.Models.SingleOrDefault(e => e.ModelId == capabilities.ModelId &&
            e.Revision == capabilities.Revision && e.Task == capabilities.Task && e.Runtime == runtime);
        if (entry is null || entry.Status != CapabilityStatus.Certified ||
            entry.Platforms is null || !entry.Platforms.Contains(platform, StringComparer.Ordinal) ||
            !string.Equals(entry.RuntimeSha256, runtimeDigest, StringComparison.OrdinalIgnoreCase))
            throw new ModelScopeException(ModelScopeErrorCode.RuntimeNotInstalled,
                "The exact model, Commit, task, runtime digest and platform must be certified by the signed Catalog.");

        if (entry.ArtifactSha256 is null || entry.ArtifactSha256.Count == 0)
            throw new ModelScopeException(ModelScopeErrorCode.DownloadIntegrityFailed, "Release artifacts require checksums.");
        if (capabilities.Artifacts.Any(a => !entry.ArtifactSha256.ContainsKey(a.Path)))
            throw new ModelScopeException(ModelScopeErrorCode.DownloadIntegrityFailed, "All inspected artifacts must be bound by the signed Catalog.");
        var root = Path.GetFullPath(capabilities.ModelPath);
        foreach (var (relative, expected) in entry.ArtifactSha256)
        {
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (Path.IsPathRooted(relative) || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ModelScopeException(ModelScopeErrorCode.DownloadIntegrityFailed, "Artifact path escapes its snapshot.");
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new ModelScopeException(ModelScopeErrorCode.DownloadIntegrityFailed, "Release artifact checksum failed.");
        }

        // Freeze caller-owned collections. Keep snapshots read-only for the lifetime of a release.
        var frozen = capabilities with
        {
            Architectures = Array.AsReadOnly(capabilities.Architectures.ToArray()),
            Artifacts = Array.AsReadOnly(capabilities.Artifacts.ToArray()),
            RuntimeCandidates = Array.AsReadOnly(capabilities.RuntimeCandidates.ToArray()),
            Warnings = Array.AsReadOnly(capabilities.Warnings.ToArray()),
        };
        return new VerifiedModelRelease(id, catalog.CatalogVersion, frozen, runtime, runtimeDigest, router);
    }

    internal Task<IModelSession> OpenAsync(CancellationToken cancellationToken) =>
        _router.CreateSessionAsync(_capabilities, Runtime, cancellationToken);
}

public sealed record ModelRolloutPolicy(
    int MinimumSuccessfulRequests = 100,
    TimeSpan? MinimumObservationTime = null,
    TimeSpan? MaximumRequestLatency = null)
{
    internal TimeSpan ObservationTime => MinimumObservationTime ?? TimeSpan.FromMinutes(5);
    internal TimeSpan LatencyLimit => MaximumRequestLatency ?? TimeSpan.FromSeconds(30);
}

public sealed record ModelRolloutSnapshot(long Generation, string StableRelease, string? CandidateRelease,
    string? PreviousRelease, int CandidatePercent, long SuccessfulRequests, string State, string? LastRollbackReason);

/// <summary>Per-model in-process rollout. Existing requests drain on their original release.</summary>
public sealed class ModelReleaseRollout
{
    private static readonly int[] Stages = [5, 25, 50, 100];
    private readonly object _sync = new();
    private readonly ModelRolloutPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _revokedIds = new(StringComparer.Ordinal);
    private VerifiedModelRelease _stable;
    private VerifiedModelRelease? _candidate;
    private VerifiedModelRelease? _previous;
    private long _generation;
    private long _successes;
    private DateTimeOffset _stageStarted;
    private int _stage;
    private string? _rollbackReason;

    public ModelReleaseRollout(VerifiedModelRelease stable, ModelRolloutPolicy? policy = null, TimeProvider? clock = null)
    {
        _stable = stable ?? throw new ArgumentNullException(nameof(stable));
        _policy = policy ?? new();
        _clock = clock ?? TimeProvider.System;
        if (_policy.MinimumSuccessfulRequests < 1 || _policy.ObservationTime < TimeSpan.Zero || _policy.LatencyLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(policy));
        _usedIds.Add(stable.Id);
    }

    public async Task StageAsync(VerifiedModelRelease candidate, ModelRequest probe,
        Func<ModelResponse, bool> validateProbe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(validateProbe);
        long generation;
        lock (_sync)
        {
            if (_candidate is not null || _usedIds.Contains(candidate.Id) || _revokedIds.Contains(candidate.Id))
                throw new InvalidOperationException("An active or previously used release cannot be staged.");
            if (candidate.ModelId != _stable.ModelId || candidate.Task != _stable.Task)
                throw new ArgumentException("Canary and stable releases must serve the same model and task.", nameof(candidate));
            generation = _generation;
        }

        var started = Stopwatch.GetTimestamp();
        await using (var session = await candidate.OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            var result = await session.InvokeAsync(probe, cancellationToken).ConfigureAwait(false);
            if (!validateProbe(result) || Stopwatch.GetElapsedTime(started) > _policy.LatencyLimit)
                throw new ModelScopeException(ModelScopeErrorCode.InferenceFailed, "Candidate preflight did not pass.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (generation != _generation || _candidate is not null || _revokedIds.Contains(candidate.Id))
                throw new InvalidOperationException("Rollout changed while the candidate was being verified.");
            _candidate = candidate;
            _usedIds.Add(candidate.Id);
            _stage = 0;
            _rollbackReason = null;
            StartWindow();
        }
    }

    public ModelRolloutSnapshot GetSnapshot()
    {
        lock (_sync) return new(_generation, _stable.Id, _candidate?.Id, _previous?.Id,
            _candidate is null ? 0 : Stages[_stage], _successes,
            _candidate is null ? "stable" : "canary", _rollbackReason);
    }

    public void Advance()
    {
        lock (_sync)
        {
            if (_candidate is null || _successes < _policy.MinimumSuccessfulRequests ||
                _clock.GetUtcNow() - _stageStarted < _policy.ObservationTime)
                throw new InvalidOperationException("This stage has not met its request-count and observation-time gates.");
            if (_stage == Stages.Length - 1)
            {
                _previous = _stable;
                _stable = _candidate;
                _candidate = null;
            }
            else _stage++;
            StartWindow();
        }
    }

    public void Rollback()
    {
        lock (_sync)
        {
            if (_candidate is null)
            {
                if (_previous is null || _revokedIds.Contains(_previous.Id))
                    throw new InvalidOperationException("No eligible previous release is retained.");
                _stable = _previous;
                _previous = null;
            }
            AbortCandidate("operator-rollback");
        }
    }

    /// <summary>Apply a numerical, performance or security revocation from the certification control plane.</summary>
    public void Revoke(string releaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        lock (_sync)
        {
            _revokedIds.Add(releaseId);
            if (_candidate?.Id == releaseId) AbortCandidate("certification-revoked");
            if (_stable.Id == releaseId)
            {
                if (_previous is not null && !_revokedIds.Contains(_previous.Id))
                {
                    _stable = _previous;
                    _previous = null;
                }
                AbortCandidate("certification-revoked");
            }
        }
    }

    public async Task<ModelResponse> InvokeAsync(string routingKey, ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        var selection = Select(routingKey, request);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var session = await selection.Release.OpenAsync(cancellationToken).ConfigureAwait(false);
            var response = await session.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            Observe(selection, Stopwatch.GetElapsedTime(started) <= _policy.LatencyLimit, "latency-regression");
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            Observe(selection, false, "runtime-failure");
            throw;
        }
    }

    public async IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(string routingKey, ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var selection = Select(routingKey, request);
        var started = Stopwatch.GetTimestamp();
        var terminal = false;
        var finished = false;
        try
        {
            await using var session = await selection.Release.OpenAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var item in session.InvokeStreamingAsync(request, cancellationToken).ConfigureAwait(false))
            {
                terminal |= item.IsTerminal;
                yield return item;
            }
            finished = true;
            if (!terminal)
                throw new ModelScopeException(ModelScopeErrorCode.InferenceFailed, "Stream ended without a terminal event.");
        }
        finally
        {
            // Early consumer disposal and explicit cancellation are not successful certification samples.
            if (!cancellationToken.IsCancellationRequested)
                Observe(selection, finished && terminal && Stopwatch.GetElapsedTime(started) <= _policy.LatencyLimit,
                    "stream-incomplete-or-slow");
        }
    }

    private Selection Select(string routingKey, ModelRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            if (_revokedIds.Contains(_stable.Id))
                throw new ModelScopeException(ModelScopeErrorCode.RuntimeNotInstalled, "The stable release is revoked and no eligible rollback exists.");
            if (request.Task != _stable.Task)
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "Request task differs from the release task.");
            var bucket = BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(routingKey))) % 100;
            var canary = _candidate is not null && bucket < Stages[_stage];
            return new(canary ? _candidate! : _stable, _generation, canary);
        }
    }

    private void Observe(Selection selection, bool success, string failure)
    {
        lock (_sync)
        {
            if (!selection.Canary || selection.Generation != _generation || !ReferenceEquals(selection.Release, _candidate)) return;
            if (!success) AbortCandidate(failure);
            else _successes++;
        }
    }

    private void AbortCandidate(string reason)
    {
        _candidate = null;
        _rollbackReason = reason;
        StartWindow();
    }

    private void StartWindow()
    {
        _generation++;
        _successes = 0;
        _stageStarted = _clock.GetUtcNow();
    }

    private sealed record Selection(VerifiedModelRelease Release, long Generation, bool Canary);
}
