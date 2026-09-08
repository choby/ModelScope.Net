using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using ModelScope.Net.Runtime;

namespace ModelScope.Net.Runtime.Python;

public sealed class LocalPythonWorkerProcessOptions
{
    public string PythonExecutable { get; set; } = "python3";

    public required string WorkerScript { get; set; }

    public string? WorkingDirectory { get; set; }

    public string Host { get; set; } = IPAddress.Loopback.ToString();

    public IList<string> Arguments { get; } = new List<string>();

    public IDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IList<string> AllowedModelRoots { get; } = new List<string>();

    public WorkerProcessSecurityOptions Security { get; } = new();

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public int DiagnosticLineLimit { get; set; } = 200;
}

public sealed record PythonWorkerProcessStatus(
    bool IsRunning,
    int? ProcessId,
    int? LastExitCode,
    IReadOnlyList<string> Diagnostics);

public sealed class LocalPythonWorkerSupervisor : IPythonWorkerSupervisor, IPythonWorkerSecurityContext, IAsyncDisposable
{
    private readonly LocalPythonWorkerProcessOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _diagnostics = new();
    private Process? _process;
    private int? _lastExitCode;
    private readonly string _apiKey;
    private string? _apiKeyFile;
    private bool _disposed;

    public LocalPythonWorkerSupervisor(LocalPythonWorkerProcessOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.PythonExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.WorkerScript);
        if (!IPAddress.TryParse(_options.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("The managed Python worker must bind to a loopback IP address.", nameof(options));
        }

        WorkerProcessSecurityPolicy.Validate(_options.Security);
        if (_options.ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Shutdown timeout must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(_options.DiagnosticLineLimit, 1);
        _apiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    }

    public string ApiKey => _apiKey;

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_process is { HasExited: false })
            {
                return;
            }

            CaptureExitAndDispose();
            _process = StartProcess();
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
            await StopProcessCoreAsync(cancellationToken).ConfigureAwait(false);
            _process = StartProcess();
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

    public PythonWorkerProcessStatus GetStatus()
    {
        var process = _process;
        var running = process is not null && !process.HasExited;
        var exitCode = process is { HasExited: true } ? process.ExitCode : _lastExitCode;
        return new PythonWorkerProcessStatus(
            running,
            running ? process!.Id : null,
            exitCode,
            _diagnostics.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopProcessCoreAsync(CancellationToken.None).ConfigureAwait(false);
            DeleteApiKeyFile();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private Process StartProcess()
    {
        RejectReservedArguments(_options.Arguments);
        var script = Path.GetFullPath(_options.WorkerScript);
        if (!File.Exists(script))
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.RuntimeNotInstalled,
                $"Python worker script '{script}' does not exist.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PythonExecutable,
            WorkingDirectory = _options.WorkingDirectory is null
                ? Path.GetDirectoryName(script)!
                : Path.GetFullPath(_options.WorkingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(script);
        foreach (var argument in _options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(_options.Host);
        startInfo.ArgumentList.Add("--api-key-file");
        startInfo.ArgumentList.Add(EnsureApiKeyFile());
        foreach (var root in _options.AllowedModelRoots)
        {
            var fullRoot = Path.GetFullPath(root);
            if (!Directory.Exists(fullRoot))
            {
                throw new ModelScopeException(
                    ModelScopeErrorCode.InvalidRequest,
                    "A configured Python worker model root does not exist.");
            }

            startInfo.ArgumentList.Add("--model-root");
            startInfo.ArgumentList.Add(fullRoot);
        }

        WorkerProcessSecurityPolicy.ApplyEnvironment(startInfo, _options.Environment, _options.Security);
        startInfo.Environment["PYTHONNOUSERSITE"] = "1";
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        startInfo.Environment["TOKENIZERS_PARALLELISM"] = "false";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => CaptureDiagnostic("stdout", eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => CaptureDiagnostic("stderr", eventArgs.Data);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Process.Start returned false.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            throw new ModelScopeException(
                ModelScopeErrorCode.RuntimeNotInstalled,
                $"Could not start Python worker with executable '{_options.PythonExecutable}'.",
                false,
                exception);
        }
    }

    private async Task StopProcessCoreAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

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

    private void CaptureExitAndDispose()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        if (process.HasExited)
        {
            _lastExitCode = process.ExitCode;
        }

        process.Dispose();
    }

    private void CaptureDiagnostic(string stream, string? value)
    {
        if (!_options.Security.CaptureDiagnostics || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var sanitized = WorkerProcessSecurityPolicy.SanitizeDiagnostic(value, _options.Security);
        _diagnostics.Enqueue($"{DateTimeOffset.UtcNow:O} {stream}: {sanitized}");
        while (_diagnostics.Count > _options.DiagnosticLineLimit)
        {
            _diagnostics.TryDequeue(out _);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void RejectReservedArguments(IEnumerable<string> arguments)
    {
        var reserved = new[]
        {
            "--host",
            "--api-key-file",
            "--model-root",
            "--tls-cert-file",
            "--tls-key-file",
            "--tls-client-ca-file",
        };
        foreach (var argument in arguments)
        {
            if (reserved.Any(name =>
                string.Equals(argument, name, StringComparison.Ordinal) ||
                argument.StartsWith(name + "=", StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"Python worker argument '{argument}' is controlled by the supervisor security policy.",
                    nameof(arguments));
            }
        }
    }

    private string EnsureApiKeyFile()
    {
        if (_apiKeyFile is not null && File.Exists(_apiKeyFile)) return _apiKeyFile;
        var path = Path.Combine(Path.GetTempPath(), $"modelscope-net-python-{Guid.NewGuid():N}.key");
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

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
            // The random key is scoped to this supervisor and becomes invalid after disposal.
        }
        catch (UnauthorizedAccessException)
        {
            // The random key is scoped to this supervisor and becomes invalid after disposal.
        }
    }
}
