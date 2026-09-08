using System.Diagnostics;
using ModelScope.Net.Runtime.Python;

namespace ModelScope.Net.Runtime.Tests;

public sealed class LocalPythonWorkerSupervisorTests
{
    [Fact]
    public async Task EnsureRunningAndRestart_RecoversAfterChildProcessCrash()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "fixtures", "python_worker_fixture.py");
        const string parentSecret = "must-not-reach-python-worker";
        var previous = Environment.GetEnvironmentVariable("MODELSCOPE_NET_TEST_PARENT_PROBE");
        Environment.SetEnvironmentVariable("MODELSCOPE_NET_TEST_PARENT_PROBE", parentSecret);
        try
        {
            await using var supervisor = new LocalPythonWorkerSupervisor(new LocalPythonWorkerProcessOptions
            {
                PythonExecutable = ResolvePythonExecutable(),
                WorkerScript = script,
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                Security = { CaptureDiagnostics = true },
            });

            await supervisor.EnsureRunningAsync();
            var first = await WaitForReadyAsync(supervisor);
            Assert.True(first.IsRunning);
            Assert.NotNull(first.ProcessId);
            Assert.Contains(first.Diagnostics, line => line.Contains("parent-probe=missing", StringComparison.Ordinal));
            Assert.DoesNotContain(first.Diagnostics, line => line.Contains(parentSecret, StringComparison.Ordinal));

            using (var process = Process.GetProcessById(first.ProcessId!.Value))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            await supervisor.EnsureRunningAsync();
            var recovered = await WaitForReadyAsync(supervisor);
            Assert.True(recovered.IsRunning);
            Assert.NotEqual(first.ProcessId, recovered.ProcessId);

            await supervisor.RestartAsync();
            var restarted = await WaitForReadyAsync(supervisor);
            Assert.True(restarted.IsRunning);
            Assert.NotEqual(recovered.ProcessId, restarted.ProcessId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MODELSCOPE_NET_TEST_PARENT_PROBE", previous);
        }
    }

    [Fact]
    public async Task EnsureRunningAsync_MissingScriptReturnsStructuredError()
    {
        await using var supervisor = new LocalPythonWorkerSupervisor(new LocalPythonWorkerProcessOptions
        {
            PythonExecutable = ResolvePythonExecutable(),
            WorkerScript = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.py"),
        });

        var error = await Assert.ThrowsAsync<ModelScopeException>(() => supervisor.EnsureRunningAsync());

        Assert.Equal(ModelScopeErrorCode.RuntimeNotInstalled, error.Code);
    }

    [Fact]
    public async Task EnsureRunningAsync_RejectsSensitiveExplicitEnvironment()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "fixtures", "python_worker_fixture.py");
        var options = new LocalPythonWorkerProcessOptions
        {
            PythonExecutable = ResolvePythonExecutable(),
            WorkerScript = script,
        };
        options.Environment["MODELSCOPE_API_TOKEN"] = "must-not-forward";
        await using var supervisor = new LocalPythonWorkerSupervisor(options);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => supervisor.EnsureRunningAsync());

        Assert.Contains("classified as sensitive", error.Message, StringComparison.Ordinal);
        Assert.False(supervisor.GetStatus().IsRunning);
    }

    [Fact]
    public async Task EnsureRunningAsync_RejectsSecurityArgumentOverride()
    {
        var options = new LocalPythonWorkerProcessOptions
        {
            PythonExecutable = ResolvePythonExecutable(),
            WorkerScript = Path.Combine(AppContext.BaseDirectory, "fixtures", "python_worker_fixture.py"),
        };
        options.Arguments.Add("--tls-cert-file=/tmp/server.crt");
        await using var supervisor = new LocalPythonWorkerSupervisor(options);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => supervisor.EnsureRunningAsync());

        Assert.Contains("controlled by the supervisor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsNonLoopbackBinding()
    {
        var options = new LocalPythonWorkerProcessOptions
        {
            PythonExecutable = ResolvePythonExecutable(),
            WorkerScript = Path.Combine(AppContext.BaseDirectory, "fixtures", "python_worker_fixture.py"),
            Security = { CaptureDiagnostics = true },
            Host = "0.0.0.0",
        };

        Assert.Throws<ArgumentException>(() => new LocalPythonWorkerSupervisor(options));
    }

    [Fact]
    public async Task ApiKeyFile_IsUserOnlyAndDeletedOnDispose()
    {
        var supervisor = new LocalPythonWorkerSupervisor(new LocalPythonWorkerProcessOptions
        {
            PythonExecutable = ResolvePythonExecutable(),
            WorkerScript = Path.Combine(AppContext.BaseDirectory, "fixtures", "python_worker_fixture.py"),
        });
        string? keyFile = null;
        try
        {
            await supervisor.EnsureRunningAsync();
            await WaitForReadyAsync(supervisor);
            keyFile = Directory.EnumerateFiles(Path.GetTempPath(), "modelscope-net-python-*.key")
                .Single(path => string.Equals(File.ReadAllText(path).Trim(), supervisor.ApiKey, StringComparison.Ordinal));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(keyFile) & (UnixFileMode.UserRead | UnixFileMode.UserWrite |
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
            }
        }
        finally
        {
            await supervisor.DisposeAsync();
        }

        Assert.NotNull(keyFile);
        Assert.False(File.Exists(keyFile));
    }

    private static async Task<PythonWorkerProcessStatus> WaitForReadyAsync(
        LocalPythonWorkerSupervisor supervisor)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var status = supervisor.GetStatus();
            if (status.Diagnostics.Any(line => line.Contains("worker-ready", StringComparison.Ordinal)))
            {
                return status;
            }

            await Task.Delay(20);
        }

        return supervisor.GetStatus();
    }

    private static string ResolvePythonExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("PYTHON");
        return string.IsNullOrWhiteSpace(configured) ? "python3" : configured;
    }
}
