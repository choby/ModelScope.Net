using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ModelScope.Net.Runtime.Python;

namespace ModelScope.Net.Runtime.Tests;

public sealed class GrpcPythonWorkerRuntimeTests
{
    [Fact]
    public async Task SlowInitialHealthPreservesStartingWorkerInsteadOfRestartingIt()
    {
        using var http = new HttpClient(new SlowHealthHandler());
        var supervisor = new CountingSupervisor();
        using var runtime = new GrpcPythonWorkerRuntime(http, new()
        {
            Endpoint = new Uri("https://127.0.0.1/"), ApiKey = "test-key", RecoveryAttempts = 3,
            RecoveryDelay = TimeSpan.FromMilliseconds(1),
        }, supervisor);
        await using var session = await runtime.CreateSessionAsync(CreateCapabilities("/tmp/model"));
        Assert.Equal(3, supervisor.EnsureCalls);
        Assert.Equal(0, supervisor.RestartCalls);
    }

    private sealed class CountingSupervisor : IPythonWorkerSupervisor
    {
        public int EnsureCalls { get; private set; }
        public int RestartCalls { get; private set; }
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) { EnsureCalls++; return Task.CompletedTask; }
        public Task RestartAsync(CancellationToken cancellationToken = default) { RestartCalls++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SlowHealthHandler : HttpMessageHandler
    {
        private int _healthCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var health = request.RequestUri!.AbsolutePath.EndsWith("/Health", StringComparison.Ordinal);
            if (health && Interlocked.Increment(ref _healthCalls) <= 2)
                return Task.FromResult(GrpcResponse([], "14"));
            Google.Protobuf.IMessage message = health
                ? new ModelScope.Net.Runtime.Python.Grpc.HealthResponse { Status = "healthy" }
                : new ModelScope.Net.Runtime.Python.Grpc.LoadModelResponse { SessionId = "session" };
            return Task.FromResult(GrpcResponse(Google.Protobuf.MessageExtensions.ToByteArray(message), "0"));
        }

        private static HttpResponseMessage GrpcResponse(byte[] message, string status)
        {
            var body = new byte[message.Length + (message.Length == 0 ? 0 : 5)];
            if (message.Length != 0)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1, 4), message.Length);
                message.CopyTo(body, 5);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20, Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = new("application/grpc"); response.TrailingHeaders.Add("grpc-status", status);
            return response;
        }
    }

    [Theory]
    [InlineData("denied", ModelScopeErrorCode.AuthenticationRequired)]
    [InlineData("allow_remote_code", ModelScopeErrorCode.RemoteCodeNotAllowed)]
    [InlineData("model_path_outside_allowed_roots", ModelScopeErrorCode.InvalidRequest)]
    public async Task RpcFailurePreservesClassificationWithoutLeakingDetailOrInnerException(string marker, ModelScopeErrorCode expected)
    {
        using var http = new HttpClient(new SensitiveRpcHandler(marker));
        var runtime = new GrpcPythonWorkerRuntime(http, new()
        {
            Endpoint = new Uri("https://127.0.0.1/"), ApiKey = "test-key", RecoveryAttempts = 0
        });
        var error = await Assert.ThrowsAsync<ModelScopeException>(() => runtime.CreateSessionAsync(CreateCapabilities("/tmp/fixture")));
        Assert.Equal(expected, error.Code);
        Assert.False(error.IsRetryable);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("fixture-secret", error.ToString());
        Assert.DoesNotContain("private prompt", error.ToString());
    }

    private sealed class SensitiveRpcHandler(string marker) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var health = request.RequestUri!.AbsolutePath.EndsWith("/Health", StringComparison.Ordinal);
            byte[] body = [];
            if (health)
            {
                var message = Google.Protobuf.MessageExtensions.ToByteArray(
                    new ModelScope.Net.Runtime.Python.Grpc.HealthResponse { Status = "healthy" });
                body = new byte[message.Length + 5];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1, 4), message.Length);
                message.CopyTo(body, 5);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20, Content = new ByteArrayContent(body)
            };
            response.Content.Headers.ContentType = new("application/grpc");
            if (health) response.TrailingHeaders.Add("grpc-status", "0");
            else
            {
                response.Headers.Add("grpc-status", "7");
                response.Headers.Add("grpc-message", Uri.EscapeDataString(marker + " fixture-secret private prompt"));
            }
            return Task.FromResult(response);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealWorker_MutualTlsAcceptsClientCertificateAndRejectsAnonymousClient()
    {
        var directory = Directory.CreateTempSubdirectory("modelscope-net-grpc-mtls-");
        Process? process = null;
        try
        {
            var certificates = CreateMutualTlsCertificates(directory.FullName);
            var modelDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "models"));
            var apiKeyPath = Path.Combine(directory.FullName, "api-key");
            await File.WriteAllTextAsync(apiKeyPath, "mtls-test-api-key");
            var port = ReservePort();
            process = StartTlsWorker(port, modelDirectory.FullName, apiKeyPath, certificates);

            var options = new PythonWorkerOptions
            {
                Endpoint = new Uri($"https://127.0.0.1:{port}"),
                Transport = PythonWorkerTransport.Grpc,
                ApiKey = "mtls-test-api-key",
                RecoveryAttempts = 0,
                RequestTimeout = TimeSpan.FromSeconds(5),
            };
            options.Tls.ClientCertificatePath = certificates.ClientCertificate;
            options.Tls.ClientPrivateKeyPath = certificates.ClientPrivateKey;
            options.Tls.TrustedCaCertificatePath = certificates.CertificateAuthority;
            using var runtime = new GrpcPythonWorkerRuntime(new HttpClient(), options);
            await WaitForHealthyAsync(runtime, process);

            var health = await runtime.CheckHealthAsync();
            Assert.True(health.IsHealthy, health.Message);
            await VerifySessionAsync(runtime, modelDirectory.FullName);

            using var anonymousHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            using var anonymousRuntime = new GrpcPythonWorkerRuntime(
                new HttpClient(anonymousHandler),
                new PythonWorkerOptions
                {
                    Endpoint = new Uri($"https://127.0.0.1:{port}"),
                    Transport = PythonWorkerTransport.Grpc,
                    ApiKey = "mtls-test-api-key",
                    RequestTimeout = TimeSpan.FromSeconds(2),
                });
            var anonymousHealth = await anonymousRuntime.CheckHealthAsync();
            Assert.False(anonymousHealth.IsHealthy);
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
            }

            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealWorker_HealthLoadInvokeStreamUnloadAndCrashRecovery_Succeeds()
    {
        var modelDirectory = Directory.CreateTempSubdirectory("modelscope-net-grpc-model-");
        try
        {
            var port = ReservePort();
            await using var supervisor = new LocalPythonWorkerSupervisor(new LocalPythonWorkerProcessOptions
            {
                PythonExecutable = ResolvePythonExecutable(),
                WorkerScript = Path.Combine(AppContext.BaseDirectory, "worker", "server.py"),
                WorkingDirectory = Path.Combine(AppContext.BaseDirectory, "worker"),
                Arguments =
                {
                    "--port", port.ToString(),
                    "--backend", "echo",
                    "--max-sessions", "1",
                    "--max-request-bytes", "1024",
                },
                AllowedModelRoots = { modelDirectory.FullName },
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                Security = { CaptureDiagnostics = true },
            });
            await supervisor.EnsureRunningAsync();
            await WaitUntilReadyAsync(supervisor);

            using var runtime = new GrpcPythonWorkerRuntime(
                new HttpClient(),
                new PythonWorkerOptions
                {
                    Endpoint = new Uri($"http://127.0.0.1:{port}"),
                    Transport = PythonWorkerTransport.Grpc,
                    RecoveryAttempts = 5,
                    RecoveryDelay = TimeSpan.FromMilliseconds(300),
                    RequestTimeout = TimeSpan.FromSeconds(10),
                },
                supervisor);

            var health = await runtime.CheckHealthAsync();
            Assert.True(health.IsHealthy, health.Message);
            Assert.Equal("1.0", health.Version);

            using (var unauthenticated = new GrpcPythonWorkerRuntime(
                new HttpClient(),
                new PythonWorkerOptions
                {
                    Endpoint = new Uri($"http://127.0.0.1:{port}"),
                    Transport = PythonWorkerTransport.Grpc,
                    RequestTimeout = TimeSpan.FromSeconds(5),
                }))
            {
                var deniedHealth = await unauthenticated.CheckHealthAsync();
                Assert.False(deniedHealth.IsHealthy);
                Assert.Contains("Unauthenticated", deniedHealth.Message, StringComparison.OrdinalIgnoreCase);
            }

            await VerifySessionAsync(runtime, modelDirectory.FullName);

            await using (var heldSession = await runtime.CreateSessionAsync(CreateCapabilities(modelDirectory.FullName)))
            {
                var limitError = await Assert.ThrowsAsync<ModelScopeException>(
                    () => runtime.CreateSessionAsync(CreateCapabilities(modelDirectory.FullName)));
                Assert.Equal(ModelScopeErrorCode.InsufficientMemory, limitError.Code);

                using var oversized = JsonDocument.Parse($$"""{ "text": "{{new string('x', 2048)}}" }""");
                var requestError = await Assert.ThrowsAsync<ModelScopeException>(() => heldSession.InvokeAsync(
                    new ModelRequest("text-generation", oversized.RootElement.Clone())));
                Assert.Equal(ModelScopeErrorCode.InsufficientMemory, requestError.Code);
            }

            var outsideRoot = Directory.CreateTempSubdirectory("modelscope-net-outside-root-");
            try
            {
                var pathError = await Assert.ThrowsAsync<ModelScopeException>(
                    () => runtime.CreateSessionAsync(CreateCapabilities(outsideRoot.FullName)));
                Assert.Equal(ModelScopeErrorCode.InvalidRequest, pathError.Code);
            }
            finally
            {
                outsideRoot.Delete(recursive: true);
            }

            var firstProcess = supervisor.GetStatus();
            Assert.True(firstProcess.IsRunning);
            using (var process = Process.GetProcessById(firstProcess.ProcessId!.Value))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            await VerifySessionAsync(runtime, modelDirectory.FullName);
            var recoveredProcess = supervisor.GetStatus();
            Assert.True(recoveredProcess.IsRunning);
            Assert.NotEqual(firstProcess.ProcessId, recoveredProcess.ProcessId);

            await File.WriteAllTextAsync(Path.Combine(modelDirectory.FullName, "remote_model.py"), "pass\n");
            var policyException = await Assert.ThrowsAsync<ModelScopeException>(
                () => runtime.CreateSessionAsync(CreateCapabilities(modelDirectory.FullName)));
            Assert.Equal(ModelScopeErrorCode.RemoteCodeNotAllowed, policyException.Code);

            using var trusted = new GrpcPythonWorkerRuntime(
                new HttpClient(),
                new PythonWorkerOptions
                {
                    Endpoint = new Uri($"http://127.0.0.1:{port}"),
                    Transport = PythonWorkerTransport.Grpc,
                    AllowRemoteCode = true,
                    RecoveryAttempts = 0,
                    RequestTimeout = TimeSpan.FromSeconds(10),
                },
                supervisor);
            await using var trustedSession = await trusted.CreateSessionAsync(
                CreateCapabilities(modelDirectory.FullName, containsRemoteCode: true));
            using var trustedPayload = JsonDocument.Parse("""{ "text": "trusted" }""");
            var trustedResponse = await trustedSession.InvokeAsync(
                new ModelRequest("text-generation", trustedPayload.RootElement.Clone()));
            Assert.True(trustedResponse.Output.GetProperty("trustRemoteCode").GetBoolean());
        }
        finally
        {
            modelDirectory.Delete(recursive: true);
        }
    }

    private static async Task VerifySessionAsync(GrpcPythonWorkerRuntime runtime, string modelPath)
    {
        await using var session = await runtime.CreateSessionAsync(CreateCapabilities(modelPath));
        using var document = JsonDocument.Parse("""{ "text": "hello" }""");
        var request = new ModelRequest(
            "text-generation",
            document.RootElement.Clone(),
            Parameters: new Dictionary<string, string> { ["temperature"] = "0" });

        var response = await session.InvokeAsync(request);
        Assert.Equal("hello", response.Output.GetProperty("payload").GetProperty("text").GetString());
        Assert.Equal("owner/model", response.Output.GetProperty("modelId").GetString());
        Assert.Equal(0, response.Output.GetProperty("parameters").GetProperty("temperature").GetInt32());
        Assert.False(response.Output.GetProperty("trustRemoteCode").GetBoolean());
        Assert.Equal("python", response.Runtime);

        var events = new List<ModelStreamEvent>();
        await foreach (var item in session.InvokeStreamingAsync(request with { Stream = true }))
        {
            events.Add(item);
        }

        Assert.Equal(2, events.Count);
        Assert.Equal("hello", events[0].Data.GetProperty("payload").GetProperty("text").GetString());
        Assert.True(events[1].IsTerminal);
        Assert.Equal(JsonValueKind.Null, events[1].Data.ValueKind);
    }

    private static ModelCapabilities CreateCapabilities(
        string modelPath,
        bool containsRemoteCode = false) => new(
        modelPath,
        "owner/model",
        "commit",
        "text-generation",
        [],
        [new ModelArtifact("model.safetensors", ModelArtifactFormat.SafeTensors, 10)],
        [new RuntimeCandidate("python", CapabilityStatus.Detected, "test", 50)],
        ContainsRemoteCode: containsRemoteCode,
        Warnings: []);

    private static async Task WaitUntilReadyAsync(LocalPythonWorkerSupervisor supervisor)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!supervisor.GetStatus().Diagnostics.Any(line => line.Contains("worker-ready", StringComparison.Ordinal)))
        {
            var status = supervisor.GetStatus();
            if (!status.IsRunning)
            {
                throw new InvalidOperationException(
                    $"Python worker exited with {status.LastExitCode}: {string.Join(Environment.NewLine, status.Diagnostics)}");
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task WaitForHealthyAsync(GrpcPythonWorkerRuntime runtime, Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"mTLS Python worker exited with code {process.ExitCode}.");
            }

            var health = await runtime.CheckHealthAsync(timeout.Token);
            if (health.IsHealthy) return;
            await Task.Delay(100, timeout.Token);
        }
    }

    private static Process StartTlsWorker(
        int port,
        string modelRoot,
        string apiKeyPath,
        MutualTlsCertificates certificates)
    {
        var workerDirectory = Path.Combine(AppContext.BaseDirectory, "worker");
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePythonExecutable(),
            WorkingDirectory = workerDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            Path.Combine(workerDirectory, "server.py"),
            "--host", "127.0.0.1",
            "--port", port.ToString(),
            "--backend", "echo",
            "--workers", "1",
            "--model-root", modelRoot,
            "--api-key-file", apiKeyPath,
            "--tls-cert-file", certificates.ServerCertificate,
            "--tls-key-file", certificates.ServerPrivateKey,
            "--tls-client-ca-file", certificates.CertificateAuthority,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Could not start mTLS Python worker.");
        return process;
    }

    private static MutualTlsCertificates CreateMutualTlsCertificates(string directory)
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddDays(1);
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            "CN=ModelScope.Net Test CA",
            caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using var ca = caRequest.CreateSelfSigned(notBefore, notAfter);

        var server = CreateSignedCertificate(
            "CN=127.0.0.1",
            ca,
            notBefore,
            notAfter,
            serverAuthentication: true);
        var client = CreateSignedCertificate(
            "CN=modelscope-net-gateway",
            ca,
            notBefore,
            notAfter,
            serverAuthentication: false);
        try
        {
            var caPath = Path.Combine(directory, "ca.crt");
            var serverCertificatePath = Path.Combine(directory, "server.crt");
            var serverKeyPath = Path.Combine(directory, "server.key");
            var clientCertificatePath = Path.Combine(directory, "client.crt");
            var clientKeyPath = Path.Combine(directory, "client.key");
            File.WriteAllText(caPath, ca.ExportCertificatePem());
            File.WriteAllText(serverCertificatePath, server.Certificate.ExportCertificatePem());
            File.WriteAllText(serverKeyPath, server.PrivateKey.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(clientCertificatePath, client.Certificate.ExportCertificatePem());
            File.WriteAllText(clientKeyPath, client.PrivateKey.ExportPkcs8PrivateKeyPem());
            return new MutualTlsCertificates(
                caPath,
                serverCertificatePath,
                serverKeyPath,
                clientCertificatePath,
                clientKeyPath);
        }
        finally
        {
            server.Certificate.Dispose();
            server.PrivateKey.Dispose();
            client.Certificate.Dispose();
            client.PrivateKey.Dispose();
        }
    }

    private static SignedCertificate CreateSignedCertificate(
        string subject,
        X509Certificate2 issuer,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        bool serverAuthentication)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        var enhancedKeyUsages = new OidCollection
        {
            new(serverAuthentication ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2"),
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(enhancedKeyUsages, true));
        if (serverAuthentication)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            names.AddIpAddress(IPAddress.Any);
            request.CertificateExtensions.Add(names.Build());
        }

        using var certificate = request.Create(
            issuer,
            notBefore,
            notAfter,
            RandomNumberGenerator.GetBytes(16));
        return new SignedCertificate(certificate.CopyWithPrivateKey(key), key);
    }

    private static string ResolvePythonExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("MODELSCOPE_WORKER_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var solutionDirectory = FindSolutionDirectory();
        var local = OperatingSystem.IsWindows()
            ? Path.Combine(solutionDirectory, ".worker-venv", "Scripts", "python.exe")
            : Path.Combine(solutionDirectory, ".worker-venv", "bin", "python");
        return File.Exists(local) ? local : "python3";
    }

    private static string FindSolutionDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ModelScope.Net.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the ModelScope.Net solution directory.");
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed record MutualTlsCertificates(
        string CertificateAuthority,
        string ServerCertificate,
        string ServerPrivateKey,
        string ClientCertificate,
        string ClientPrivateKey);

    private sealed record SignedCertificate(X509Certificate2 Certificate, RSA PrivateKey);
}
