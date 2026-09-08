using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using ModelScope.Net.Runtime;

namespace ModelScope.Net.Runtime.Gguf;

public sealed class LlamaServerProcessOptions
{
    public required string Executable { get; set; }

    public string? WorkingDirectory { get; set; }

    public string Host { get; set; } = IPAddress.Loopback.ToString();

    public int Port { get; set; } = 8080;

    public string ModelAlias { get; set; } = "modelscope-net";

    public string? ApiKey { get; set; }

    public int ContextSize { get; set; } = 512;

    public int Threads { get; set; } = Math.Max(1, System.Environment.ProcessorCount / 2);

    public int GpuLayers { get; set; }

    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan HealthPollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    public int DiagnosticLineLimit { get; set; } = 300;

    public IList<string> PrefixArguments { get; } = new List<string>();

    public IList<string> ExtraArguments { get; } = new List<string>();

    public IDictionary<string, string> Environment { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public WorkerProcessSecurityOptions Security { get; } = new();
}

public sealed record LlamaServerProcessStatus(
    bool IsRunning,
    int? ProcessId,
    int? LastExitCode,
    string? ModelFile,
    Uri Endpoint,
    IReadOnlyList<string> Diagnostics);

public interface ILlamaServerSupervisor
{
    Uri Endpoint { get; }

    string ModelAlias { get; }

    string ApiKey { get; }

    Task EnsureRunningAsync(string modelFile, CancellationToken cancellationToken = default);

    Task RestartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    LlamaServerProcessStatus GetStatus();
}

public sealed class LocalLlamaServerSupervisor : ILlamaServerSupervisor, IAsyncDisposable
{
    private readonly LlamaServerProcessOptions _options;
    private readonly HttpClient _healthClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _diagnostics = new();
    private Process? _process;
    private string? _modelFile;
    private int? _lastExitCode;
    private readonly string _apiKey;
    private string? _apiKeyFile;
    private bool _disposed;

    public LocalLlamaServerSupervisor(LlamaServerProcessOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.Executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ModelAlias);
        WorkerProcessSecurityPolicy.Validate(_options.Security);
        if (!IPAddress.TryParse(_options.Host, out var address) || !IPAddress.IsLoopback(address))
            throw new ArgumentException("The managed llama-server must bind to a loopback IP address.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.Port, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ContextSize, 32);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Threads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.GpuLayers, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.DiagnosticLineLimit, 1);
        if (_options.StartupTimeout <= TimeSpan.Zero ||
            _options.ShutdownTimeout <= TimeSpan.Zero ||
            _options.HealthPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Process timeouts must be positive.");
        }

        Endpoint = new UriBuilder(Uri.UriSchemeHttp, _options.Host, _options.Port).Uri;
        _apiKey = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))
            : _options.ApiKey;
    }

    public Uri Endpoint { get; }

    public string ModelAlias => _options.ModelAlias;

    public string ApiKey => _apiKey;

    public async Task EnsureRunningAsync(
        string modelFile,
        CancellationToken cancellationToken = default)
    {
        var fullModelFile = Path.GetFullPath(modelFile);
        if (!File.Exists(fullModelFile))
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.ModelNotFound,
                "The configured GGUF model file does not exist.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_process is { HasExited: false } &&
                string.Equals(_modelFile, fullModelFile, StringComparison.Ordinal))
            {
                await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await StopProcessCoreAsync(cancellationToken).ConfigureAwait(false);
            _modelFile = fullModelFile;
            _process = StartProcess(fullModelFile);
            try
            {
                await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopProcessCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var modelFile = _modelFile ?? throw new ModelScopeException(
                ModelScopeErrorCode.InvalidRequest,
                "llama-server has not been assigned a model file.");
            await StopProcessCoreAsync(cancellationToken).ConfigureAwait(false);
            _modelFile = modelFile;
            _process = StartProcess(modelFile);
            try
            {
                await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopProcessCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopProcessCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public LlamaServerProcessStatus GetStatus()
    {
        var process = _process;
        var running = process is not null && !process.HasExited;
        var exitCode = process is { HasExited: true } ? process.ExitCode : _lastExitCode;
        return new LlamaServerProcessStatus(
            running,
            running ? process!.Id : null,
            exitCode,
            _modelFile,
            Endpoint,
            _diagnostics.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopProcessCoreAsync(CancellationToken.None).ConfigureAwait(false);
            DeleteApiKeyFile();
            _healthClient.Dispose();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private Process StartProcess(string modelFile)
    {
        RejectReservedArguments(_options.ExtraArguments);
        var executable = ResolveExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = _options.WorkingDirectory is null
                ? Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
                : Path.GetFullPath(_options.WorkingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in _options.PrefixArguments) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(modelFile);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(_options.Host);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(_options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--alias");
        startInfo.ArgumentList.Add(_options.ModelAlias);
        startInfo.ArgumentList.Add("--ctx-size");
        startInfo.ArgumentList.Add(_options.ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--threads");
        startInfo.ArgumentList.Add(_options.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--n-gpu-layers");
        startInfo.ArgumentList.Add(_options.GpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--no-webui");
        startInfo.ArgumentList.Add("--no-slots");
        startInfo.ArgumentList.Add("--api-key-file");
        startInfo.ArgumentList.Add(EnsureApiKeyFile());
        foreach (var argument in _options.ExtraArguments) startInfo.ArgumentList.Add(argument);
        WorkerProcessSecurityPolicy.ApplyEnvironment(startInfo, _options.Environment, _options.Security);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => CaptureDiagnostic("stdout", eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => CaptureDiagnostic("stderr", eventArgs.Data);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Process.Start returned false.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            throw new ModelScopeException(
                ModelScopeErrorCode.RuntimeNotInstalled,
                "The configured llama-server executable could not be started.",
                innerException: exception);
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("llama-server was not started.");
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(_options.StartupTimeout);
        while (true)
        {
            if (process.HasExited)
            {
                _lastExitCode = process.ExitCode;
                throw new ModelScopeException(
                    ModelScopeErrorCode.ModelLoadFailed,
                    $"llama-server exited with code {process.ExitCode} while loading the GGUF model. {LastDiagnostic()}");
            }

            try
            {
                using var response = await _healthClient.GetAsync(
                    new Uri(Endpoint, "health"),
                    startup.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // The listener may not be ready yet.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.ModelLoadFailed,
                    $"llama-server did not become ready within {_options.StartupTimeout}. {LastDiagnostic()}",
                    isRetryable: true);
            }

            try
            {
                await Task.Delay(_options.HealthPollInterval, startup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.ModelLoadFailed,
                    $"llama-server did not become ready within {_options.StartupTimeout}. {LastDiagnostic()}",
                    isRetryable: true);
            }
        }
    }

    private async Task StopProcessCoreAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.ShutdownTimeout);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            _lastExitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // The process exited between checks.
        }
        finally
        {
            process.Dispose();
        }
    }

    private string ResolveExecutable()
    {
        var value = _options.Executable;
        if (value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        {
            var path = Path.GetFullPath(value);
            if (!File.Exists(path))
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.RuntimeNotInstalled,
                    "The configured llama-server executable does not exist.");
            }
            return path;
        }
        return value;
    }

    private static void RejectReservedArguments(IEnumerable<string> arguments)
    {
        var reserved = new[]
        {
            "--host",
            "--port",
            "--model",
            "-m",
            "--api-key",
            "--api-key-file",
            "--webui",
            "--no-webui",
            "--slots",
            "--no-slots",
        };
        foreach (var argument in arguments)
        {
            if (reserved.Any(name =>
                string.Equals(argument, name, StringComparison.Ordinal) ||
                argument.StartsWith(name + "=", StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"llama-server argument '{argument}' is controlled by the supervisor security policy.",
                    nameof(arguments));
            }
        }
    }

    private string EnsureApiKeyFile()
    {
        if (_apiKeyFile is not null && File.Exists(_apiKeyFile)) return _apiKeyFile;
        var path = Path.Combine(Path.GetTempPath(), $"modelscope-net-llama-{Guid.NewGuid():N}.key");
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, fileOptions))
        using (var writer = new StreamWriter(stream))
        {
            writer.WriteLine(_apiKey);
        }
        _apiKeyFile = path;
        return path;
    }

    private void DeleteApiKeyFile()
    {
        var path = _apiKeyFile;
        _apiKeyFile = null;
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the random key is only valid for this disposed supervisor.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; the random key is only valid for this disposed supervisor.
        }
    }

    private void CaptureDiagnostic(string streamName, string? value)
    {
        if (!_options.Security.CaptureDiagnostics || string.IsNullOrWhiteSpace(value)) return;
        var sanitized = WorkerProcessSecurityPolicy.SanitizeDiagnostic(value, _options.Security);
        _diagnostics.Enqueue($"{DateTimeOffset.UtcNow:O} {streamName}: {sanitized}");
        while (_diagnostics.Count > _options.DiagnosticLineLimit) _diagnostics.TryDequeue(out _);
    }

    private string LastDiagnostic() => _diagnostics.LastOrDefault() ?? "No diagnostic output was captured.";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
