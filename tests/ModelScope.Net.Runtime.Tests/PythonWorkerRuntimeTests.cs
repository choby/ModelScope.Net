using System.Net;
using System.Text;
using System.Text.Json;
using ModelScope.Net.Runtime.Python;

namespace ModelScope.Net.Runtime.Tests;

public sealed class PythonWorkerRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpFailureDoesNotEchoSensitiveBody(bool streaming)
    {
        var runtime = CreateRuntime(new StubHandler((Func<HttpRequestMessage, HttpResponseMessage>)(request =>
        {
            if (request.Method == HttpMethod.Get) return Json("""{"status":"healthy","version":"1.0"}""");
            if (request.RequestUri!.AbsolutePath.EndsWith("/load", StringComparison.Ordinal)) return Json("""{"sessionId":"fixture"}""");
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            return new(HttpStatusCode.Forbidden) { Content = new StringContent("Bearer fixture-secret private prompt token=sensitive") };
        })));
        await using var session = await runtime.CreateSessionAsync(CreateCapabilities());
        var request = new ModelRequest("text-classification", JsonSerializer.SerializeToElement("input"));
        var error = await Assert.ThrowsAsync<ModelScopeException>(async () =>
        {
            if (streaming) { await foreach (var item in session.InvokeStreamingAsync(request)) { } }
            else await session.InvokeAsync(request);
        });
        Assert.Equal(ModelScopeErrorCode.InferenceFailed, error.Code);
        Assert.False(error.IsRetryable);
        Assert.DoesNotContain("fixture-secret", error.ToString());
        Assert.DoesNotContain("private prompt", error.ToString());
    }

    [Fact]
    public async Task WorkerProtocol_HealthLoadInvokeStreamAndDispose_Succeeds()
    {
        var deleted = false;
        var handler = new StubHandler(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("worker-test-key", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/health")
            {
                return Json("""{ "status": "healthy", "version": "1.0" }""");
            }

            if (request.Method == HttpMethod.Post && path == "/v1/models/load")
            {
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("owner/model", body);
                return Json("""{ "sessionId": "session-1" }""");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/invoke", StringComparison.Ordinal))
            {
                return Json("""{ "label": "positive" }""");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/stream", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"token\":\"hello\"}\n\ndata: [DONE]\n\n",
                        Encoding.UTF8,
                        "text/event-stream"),
                };
            }

            if (request.Method == HttpMethod.Delete)
            {
                deleted = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {path}");
        });
        var runtime = CreateRuntime(handler);

        var health = await runtime.CheckHealthAsync();
        await using (var session = await runtime.CreateSessionAsync(CreateCapabilities()))
        {
            using var payload = JsonDocument.Parse("""{ "text": "demo" }""");
            var response = await session.InvokeAsync(new ModelRequest("text-classification", payload.RootElement.Clone()));
            Assert.Equal("positive", response.Output.GetProperty("label").GetString());

            var events = new List<ModelStreamEvent>();
            await foreach (var item in session.InvokeStreamingAsync(
                new ModelRequest("text-generation", payload.RootElement.Clone(), Stream: true)))
            {
                events.Add(item);
            }

            Assert.Equal(2, events.Count);
            Assert.True(events[^1].IsTerminal);
        }

        Assert.True(health.IsHealthy);
        Assert.Equal("1.0", health.Version);
        Assert.True(deleted);
    }

    [Fact]
    public async Task CreateSessionAsync_RestartsWorkerAfterTransientHealthFailure()
    {
        var healthCalls = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/health")
            {
                if (Interlocked.Increment(ref healthCalls) == 1)
                {
                    throw new HttpRequestException("worker stopped");
                }

                return Task.FromResult(Json("""{ "status": "ready" }"""));
            }

            return Task.FromResult(Json("""{ "sessionId": "recovered" }"""));
        });
        var supervisor = new FakeSupervisor();
        var runtime = CreateRuntime(handler, supervisor);

        await using var session = await runtime.CreateSessionAsync(CreateCapabilities());

        Assert.Equal(1, supervisor.EnsureCalls);
        Assert.Equal(1, supervisor.RestartCalls);
        Assert.Equal(2, healthCalls);
    }

    [Fact]
    public void Evaluate_RejectsRemoteCodeByDefault()
    {
        var runtime = CreateRuntime(new StubHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException())));
        var candidate = runtime.Evaluate(CreateCapabilities(containsRemoteCode: true));

        Assert.Equal(CapabilityStatus.Unsupported, candidate.Status);
    }

    [Fact]
    public void Constructor_RejectsClearTextRemoteEndpoint()
    {
        var options = new PythonWorkerOptions { Endpoint = new Uri("http://worker.example/") };

        Assert.Throws<ArgumentException>(() => new PythonWorkerRuntime(new HttpClient(), options));
        Assert.Throws<ArgumentException>(() => new GrpcPythonWorkerRuntime(new HttpClient(), options));
    }

    [Fact]
    public void Constructor_RequiresCompleteMutualTlsForRemoteEndpoint()
    {
        var missing = new PythonWorkerOptions { Endpoint = new Uri("https://worker.example/") };
        var partial = new PythonWorkerOptions { Endpoint = new Uri("https://worker.example/") };
        partial.Tls.ClientCertificatePath = "/run/modelscope/tls/client.crt";
        var complete = new PythonWorkerOptions { Endpoint = new Uri("https://worker.example/") };
        complete.Tls.ClientCertificatePath = "/run/modelscope/tls/client.crt";
        complete.Tls.ClientPrivateKeyPath = "/run/modelscope/tls/client.key";
        complete.Tls.TrustedCaCertificatePath = "/run/modelscope/tls/ca.crt";

        Assert.Throws<ArgumentException>(() => new PythonWorkerRuntime(new HttpClient(), missing));
        Assert.Throws<ArgumentException>(() => new GrpcPythonWorkerRuntime(new HttpClient(), partial));
        Assert.Throws<InvalidOperationException>(() => new PythonWorkerRuntime(new HttpClient(), complete));
    }

    private static PythonWorkerRuntime CreateRuntime(
        HttpMessageHandler handler,
        IPythonWorkerSupervisor? supervisor = null) => new(
        new HttpClient(handler),
        new PythonWorkerOptions
        {
            Endpoint = new Uri("https://127.0.0.1/"),
            ApiKey = "worker-test-key",
            RecoveryAttempts = 1,
            RecoveryDelay = TimeSpan.Zero,
        },
        supervisor);

    private static ModelCapabilities CreateCapabilities(bool containsRemoteCode = false) => new(
        "/models/demo",
        "owner/model",
        "commit",
        "text-classification",
        [],
        [new ModelArtifact("model.safetensors", ModelArtifactFormat.SafeTensors, 10)],
        [new RuntimeCandidate("python", CapabilityStatus.Detected, "test", 50)],
        containsRemoteCode,
        []);

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
            : this(request => Task.FromResult(callback(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request);
    }

    private sealed class FakeSupervisor : IPythonWorkerSupervisor
    {
        public int EnsureCalls { get; private set; }

        public int RestartCalls { get; private set; }

        public Task EnsureRunningAsync(CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            return Task.CompletedTask;
        }

        public Task RestartAsync(CancellationToken cancellationToken = default)
        {
            RestartCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
