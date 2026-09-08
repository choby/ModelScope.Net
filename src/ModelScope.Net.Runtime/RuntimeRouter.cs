namespace ModelScope.Net.Runtime;

public sealed class RuntimeRouter
{
    private readonly IReadOnlyDictionary<string, IModelRuntime> _runtimes;
    private readonly RuntimeRouterOptions _options;
    private readonly IModelSessionInstrumentation? _instrumentation;

    public RuntimeRouter(
        IEnumerable<IModelRuntime> runtimes,
        RuntimeRouterOptions? options = null,
        IModelSessionInstrumentation? instrumentation = null)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        _runtimes = runtimes.ToDictionary(runtime => runtime.Name, StringComparer.OrdinalIgnoreCase);
        // Freeze the configured policy so later options mutation cannot weaken an existing router.
        _options = new RuntimeRouterOptions
        {
            Mode = options?.Mode ?? RuntimePolicyMode.Development,
            AllowExperimentalInProduction = options?.AllowExperimentalInProduction ?? false,
            AllowRemoteFallback = options?.AllowRemoteFallback ?? false,
            AllowRemoteCode = options?.AllowRemoteCode ?? false,
            ProductionPolicy = options?.ProductionPolicy,
        };
        _instrumentation = instrumentation;
    }

    public IReadOnlyList<RuntimeCandidate> Evaluate(ModelCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return _runtimes.Values
            .Select(runtime => runtime.Evaluate(capabilities))
            .Select(candidate => _options.Mode == RuntimePolicyMode.Production &&
                candidate.Status is not CapabilityStatus.Unavailable and not CapabilityStatus.Unsupported
                    ? candidate with
                    {
                        Status = _options.ProductionPolicy?.IsCertified(capabilities, candidate.RuntimeName) == true
                            ? CapabilityStatus.Certified
                            : candidate.Status == CapabilityStatus.Certified ? CapabilityStatus.Compatible : candidate.Status,
                        Reason = _options.ProductionPolicy?.IsCertified(capabilities, candidate.RuntimeName) == true
                            ? "Exact signed production certification matched; artifacts are checked at session creation."
                            : "No exact signed production certification matches this deployment.",
                    }
                    : candidate)
            .OrderBy(candidate => IsRemote(candidate.RuntimeName))
            .ThenByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.RuntimeName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IModelSession> CreateSessionAsync(
        ModelCapabilities capabilities,
        string? preferredRuntime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.ContainsRemoteCode && !_options.AllowRemoteCode)
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.RemoteCodeNotAllowed,
                "The model contains executable code and the current policy does not allow it.");
        }

        var candidates = Evaluate(capabilities);
        if (!string.IsNullOrWhiteSpace(preferredRuntime))
        {
            candidates = candidates
                .Where(candidate => string.Equals(candidate.RuntimeName, preferredRuntime, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var remoteOnlyAfterFailure = false;
        foreach (var candidate in candidates)
        {
            if (_options.Mode == RuntimePolicyMode.Production &&
                _options.ProductionPolicy?.IsRevokedOrExpired(capabilities, candidate.RuntimeName) == true) continue;
            if (remoteOnlyAfterFailure && !IsRemote(candidate.RuntimeName)) continue;
            if (IsRemote(candidate.RuntimeName) && string.IsNullOrWhiteSpace(preferredRuntime) && !_options.AllowRemoteFallback)
                continue;
            if (!IsAllowed(candidate) || !_runtimes.TryGetValue(candidate.RuntimeName, out var runtime))
            {
                continue;
            }

            // Integrity failures must never be swallowed as load failures or trigger data egress.
            if (_options.Mode == RuntimePolicyMode.Production && candidate.Status == CapabilityStatus.Certified)
                await _options.ProductionPolicy!.VerifyArtifactsAsync(capabilities, candidate.RuntimeName, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var session = await runtime.CreateSessionAsync(capabilities, cancellationToken).ConfigureAwait(false);
                if (_options.Mode == RuntimePolicyMode.Production)
                    session = new TaskBoundSession(session, capabilities.Task, () =>
                        candidate.Status == CapabilityStatus.Certified
                            ? _options.ProductionPolicy!.IsCertified(capabilities, candidate.RuntimeName)
                            : _options.ProductionPolicy?.IsRevokedOrExpired(capabilities, candidate.RuntimeName) != true);
                return _instrumentation?.Instrument(session, capabilities, candidate.RuntimeName) ?? session;
            }
            catch (ModelScopeException exception) when (
                exception.IsRetryable &&
                (_options.Mode == RuntimePolicyMode.Development ||
                 (_options.AllowRemoteFallback && string.IsNullOrWhiteSpace(preferredRuntime))))
            {
                // Production opt-in only permits moving to an independently certified remote target.
                remoteOnlyAfterFailure = _options.Mode == RuntimePolicyMode.Production;
            }
        }

        throw new ModelScopeException(
            ModelScopeErrorCode.RuntimeNotInstalled,
            preferredRuntime is null
                ? "No runtime allowed by the current policy can load this model."
                : $"Runtime '{preferredRuntime}' is unavailable, unsupported, or disallowed.");
    }

    private bool IsRemote(string runtimeName) =>
        string.Equals(runtimeName, "remote", StringComparison.OrdinalIgnoreCase) ||
        (_runtimes.TryGetValue(runtimeName, out var runtime) && runtime is IRemoteModelRuntime);

    private sealed class TaskBoundSession(IModelSession inner, string? certifiedTask, Func<bool> remainsAuthorized) : IModelSession
    {
        public ModelCapabilities Capabilities => inner.Capabilities;

        public async Task<ModelResponse> InvokeAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Validate(request);
            var response = await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            Validate(request);
            return response;
        }

        public IAsyncEnumerable<ModelStreamEvent> InvokeStreamingAsync(
            ModelRequest request, CancellationToken cancellationToken = default)
        {
            Validate(request);
            return Stream(request, cancellationToken);
        }

        private async IAsyncEnumerable<ModelStreamEvent> Stream(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Validate(request);
            await foreach (var item in inner.InvokeStreamingAsync(request, cancellationToken).ConfigureAwait(false))
            {
                Validate(request);
                yield return item;
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private void Validate(ModelRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!remainsAuthorized())
                throw new ModelScopeException(ModelScopeErrorCode.RuntimeNotInstalled, "The session certification has been revoked or expired.");
            if (string.IsNullOrWhiteSpace(certifiedTask) || !string.Equals(request.Task, certifiedTask, StringComparison.Ordinal))
                throw new ModelScopeException(ModelScopeErrorCode.InvalidRequest, "The request task is not authorized by this model session.");
        }
    }

    private bool IsAllowed(RuntimeCandidate candidate)
    {
        if (candidate.Status is CapabilityStatus.Unavailable or CapabilityStatus.Unsupported)
        {
            return false;
        }

        if (_options.Mode == RuntimePolicyMode.Development)
        {
            return true;
        }

        return candidate.Status == CapabilityStatus.Certified ||
            (_options.AllowExperimentalInProduction &&
             candidate.Status is CapabilityStatus.LoadVerified or CapabilityStatus.Compatible);
    }
}
