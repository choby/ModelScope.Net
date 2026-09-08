using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ModelScope.Net.Runtime.Gguf;

namespace ModelScope.Net.Runtime.Tests;

public sealed class GgufRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpFailureDoesNotEchoSensitiveBody(bool streaming)
    {
        var directory = CreateModelDirectory("qwen2", fileType: 10);
        try
        {
            var runtime = CreateRuntime(new StubHandler(request => Task.FromResult(request.Method == HttpMethod.Get
                ? Json("""{"status":"ok"}""")
                : new(HttpStatusCode.Forbidden) { Content = new StringContent("Bearer fixture-secret private prompt token=sensitive") })));
            await using var session = await runtime.CreateSessionAsync(CreateCapabilities(directory));
            var request = new ModelRequest("chat", JsonSerializer.SerializeToElement(new { messages = Array.Empty<object>() }));
            var error = await Assert.ThrowsAsync<ModelScopeException>(async () =>
            {
                if (streaming) { await foreach (var item in session.InvokeStreamingAsync(request)) { } }
                else await session.InvokeAsync(request);
            });
            Assert.Equal(ModelScopeErrorCode.AuthenticationRequired, error.Code);
            Assert.False(error.IsRetryable);
            Assert.DoesNotContain("fixture-secret", error.ToString());
            Assert.DoesNotContain("private prompt", error.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task InvokeAsync_UsesChatRouteAliasAndResourceLimits()
    {
        var directory = CreateModelDirectory("qwen2", fileType: 10);
        try
        {
            var handler = new StubHandler(async request =>
            {
                if (request.Method == HttpMethod.Get)
                    return Json("""{ "status": "ok" }""");

                Assert.Equal("http://127.0.0.1:18080/v1/chat/completions", request.RequestUri?.ToString());
                Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("test-alias", payload.RootElement.GetProperty("model").GetString());
                Assert.Equal(32, payload.RootElement.GetProperty("max_tokens").GetInt32());
                Assert.False(payload.RootElement.GetProperty("stream").GetBoolean());
                return Json("""{ "choices": [{ "message": { "content": "ok" } }] }""");
            });
            var runtime = CreateRuntime(handler);
            await using var session = await runtime.CreateSessionAsync(CreateCapabilities(directory));
            using var payload = JsonDocument.Parse("""{ "messages": [{ "role": "user", "content": "hi" }] }""");

            var response = await session.InvokeAsync(new ModelRequest("chat", payload.RootElement.Clone()));

            Assert.Equal("gguf", response.Runtime);
            Assert.Equal("ok", response.Output.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

            using var oversized = JsonDocument.Parse("""{ "messages": [], "max_tokens": 65 }""");
            var error = await Assert.ThrowsAsync<ModelScopeException>(() => session.InvokeAsync(
                new ModelRequest("chat", oversized.RootElement.Clone())));
            Assert.Equal(ModelScopeErrorCode.InvalidRequest, error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeStreamingAsync_ParsesOpenAiServerSentEvents()
    {
        var directory = CreateModelDirectory("qwen2", fileType: 10);
        try
        {
            var handler = new StubHandler(request => Task.FromResult(
                request.Method == HttpMethod.Get
                    ? Json("""{ "status": "ok" }""")
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\ndata: [DONE]\n\n",
                            Encoding.UTF8,
                            "text/event-stream"),
                    }));
            var runtime = CreateRuntime(handler);
            await using var session = await runtime.CreateSessionAsync(CreateCapabilities(directory));
            using var payload = JsonDocument.Parse("""{ "messages": [] }""");

            var events = new List<ModelStreamEvent>();
            await foreach (var item in session.InvokeStreamingAsync(
                new ModelRequest("chat", payload.RootElement.Clone(), Stream: true)))
            {
                events.Add(item);
            }

            Assert.Equal(2, events.Count);
            Assert.False(events[0].IsTerminal);
            Assert.Equal("hello", events[0].Data.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
            Assert.True(events[1].IsTerminal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_UsesEmbeddingRouteAndMapsTextInput()
    {
        var directory = CreateModelDirectory("bert", fileType: 15, tokenizer: "bert", includeChatTemplate: false);
        try
        {
            var handler = new StubHandler(async request =>
            {
                if (request.Method == HttpMethod.Get)
                    return Json("""{ "status": "ok" }""");

                Assert.Equal("http://127.0.0.1:18080/v1/embeddings", request.RequestUri?.ToString());
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("test-alias", payload.RootElement.GetProperty("model").GetString());
                Assert.Equal("hello", payload.RootElement.GetProperty("input").GetString());
                Assert.False(payload.RootElement.TryGetProperty("max_tokens", out _));
                Assert.False(payload.RootElement.TryGetProperty("stream", out _));
                return Json("""{ "data": [{ "embedding": [0.1, 0.2], "index": 0 }] }""");
            });
            var runtime = CreateRuntime(handler);
            var capabilities = CreateCapabilities(directory) with
            {
                Task = "feature-extraction",
                Architectures = ["bert"],
            };
            await using var session = await runtime.CreateSessionAsync(capabilities);
            var response = await session.InvokeAsync(new ModelRequest(
                "feature-extraction",
                JsonSerializer.SerializeToElement(new { text = "hello" })));

            Assert.Equal("gguf", response.Runtime);
            Assert.Equal(2, response.Output.GetProperty("data")[0].GetProperty("embedding").GetArrayLength());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateSessionAsync_RejectsArchitectureOutsideHeaderPolicy()
    {
        var directory = CreateModelDirectory("llama", fileType: 10);
        try
        {
            var runtime = CreateRuntime(new StubHandler(_ => Task.FromResult(Json("{}"))));

            var error = await Assert.ThrowsAsync<ModelScopeException>(() =>
                runtime.CreateSessionAsync(CreateCapabilities(directory)));

            Assert.Equal(ModelScopeErrorCode.ArchitectureUnsupported, error.Code);
            Assert.Contains("llama", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LocalSupervisor_RecoversAfterServerCrashAndStopsProcessTree()
    {
        var modelFile = Path.Combine(Path.GetTempPath(), $"modelscope-net-gguf-{Guid.NewGuid():N}.gguf");
        await File.WriteAllTextAsync(modelFile, "fixture");
        const string parentSecret = "must-not-reach-llama-server";
        var previous = Environment.GetEnvironmentVariable("MODELSCOPE_NET_TEST_LLAMA_PARENT_PROBE");
        Environment.SetEnvironmentVariable("MODELSCOPE_NET_TEST_LLAMA_PARENT_PROBE", parentSecret);
        var options = new LlamaServerProcessOptions
        {
            Executable = ResolvePythonExecutable(),
            Port = GetFreePort(),
            StartupTimeout = TimeSpan.FromSeconds(10),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            Security = { CaptureDiagnostics = true },
        };
        options.PrefixArguments.Add(Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "llama_server_fixture.py"));
        await using var supervisor = new LocalLlamaServerSupervisor(options);
        try
        {
            await supervisor.EnsureRunningAsync(modelFile);
            var first = supervisor.GetStatus();
            Assert.True(first.IsRunning);
            Assert.NotNull(first.ProcessId);
            Assert.Contains(first.Diagnostics, line => line.Contains("parent-probe=missing", StringComparison.Ordinal));
            Assert.DoesNotContain(first.Diagnostics, line => line.Contains(parentSecret, StringComparison.Ordinal));

            using (var process = Process.GetProcessById(first.ProcessId!.Value))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            await supervisor.EnsureRunningAsync(modelFile);
            var recovered = supervisor.GetStatus();
            Assert.True(recovered.IsRunning);
            Assert.NotEqual(first.ProcessId, recovered.ProcessId);

            await supervisor.StopAsync();
            Assert.False(supervisor.GetStatus().IsRunning);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MODELSCOPE_NET_TEST_LLAMA_PARENT_PROBE", previous);
            File.Delete(modelFile);
        }
    }

    [Fact]
    public void LocalSupervisor_RejectsNonLoopbackBinding()
    {
        var options = new LlamaServerProcessOptions
        {
            Executable = ResolvePythonExecutable(),
            Host = "0.0.0.0",
        };

        Assert.Throws<ArgumentException>(() => new LocalLlamaServerSupervisor(options));
    }

    [Fact]
    public async Task LocalSupervisor_RejectsSensitiveExplicitEnvironment()
    {
        var options = new LlamaServerProcessOptions
        {
            Executable = ResolvePythonExecutable(),
            Port = GetFreePort(),
        };
        options.PrefixArguments.Add(Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "llama_server_fixture.py"));
        options.Environment["AWS_SECRET_ACCESS_KEY"] = "must-not-forward";
        await using var supervisor = new LocalLlamaServerSupervisor(options);
        var modelFile = Path.Combine(Path.GetTempPath(), $"modelscope-net-gguf-{Guid.NewGuid():N}.gguf");
        await File.WriteAllTextAsync(modelFile, "fixture");
        try
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => supervisor.EnsureRunningAsync(modelFile));
            Assert.Contains("classified as sensitive", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(modelFile);
        }
    }

    [Fact]
    public async Task LocalSupervisor_RejectsSecurityArgumentOverride()
    {
        var options = new LlamaServerProcessOptions
        {
            Executable = ResolvePythonExecutable(),
            Port = GetFreePort(),
        };
        options.PrefixArguments.Add(Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "llama_server_fixture.py"));
        options.ExtraArguments.Add("--host=0.0.0.0");
        await using var supervisor = new LocalLlamaServerSupervisor(options);
        var modelFile = Path.Combine(Path.GetTempPath(), $"modelscope-net-gguf-{Guid.NewGuid():N}.gguf");
        await File.WriteAllTextAsync(modelFile, "fixture");
        try
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => supervisor.EnsureRunningAsync(modelFile));
            Assert.Contains("controlled by the supervisor", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(modelFile);
        }
    }

    private static GgufRuntimeAdapter CreateRuntime(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new GgufRuntimeOptions
        {
            LlamaServerEndpoint = new Uri("http://127.0.0.1:18080/"),
            ModelAlias = "test-alias",
            ApiKey = "test-key",
            DefaultMaxTokens = 32,
            MaxTokens = 64,
        });

    private static ModelCapabilities CreateCapabilities(string directory) => new(
        directory,
        "tests/qwen-gguf",
        "fixture-v1",
        "text-generation",
        ["qwen2"],
        [new ModelArtifact("model.gguf", ModelArtifactFormat.Gguf, new FileInfo(Path.Combine(directory, "model.gguf")).Length)],
        [new RuntimeCandidate("gguf", CapabilityStatus.Compatible, "test", 90)],
        ContainsRemoteCode: false,
        Warnings: []);

    private static string CreateModelDirectory(
        string architecture,
        uint fileType,
        string tokenizer = "gpt2",
        bool includeChatTemplate = true)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"modelscope-net-gguf-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, "model.gguf"));
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write("GGUF"u8);
        writer.Write(3u);
        writer.Write(0ul);
        writer.Write(includeChatTemplate ? 5ul : 4ul);
        WriteString(writer, "general.architecture");
        writer.Write(8u);
        WriteString(writer, architecture);
        WriteString(writer, "general.file_type");
        writer.Write(4u);
        writer.Write(fileType);
        WriteString(writer, "general.quantization_version");
        writer.Write(4u);
        writer.Write(2u);
        WriteString(writer, "tokenizer.ggml.model");
        writer.Write(8u);
        WriteString(writer, tokenizer);
        if (includeChatTemplate)
        {
            WriteString(writer, "tokenizer.chat_template");
            writer.Write(8u);
            WriteString(writer, "{{ messages }}");
        }
        return directory;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ResolvePythonExecutable()
    {
        var configured = System.Environment.GetEnvironmentVariable("PYTHON");
        return string.IsNullOrWhiteSpace(configured) ? "python3" : configured;
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request);
    }
}
