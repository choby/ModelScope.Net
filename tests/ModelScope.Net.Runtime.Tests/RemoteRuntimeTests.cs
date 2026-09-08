using System.Net;
using System.Text;
using System.Text.Json;
using ModelScope.Net.Runtime.Remote;

namespace ModelScope.Net.Runtime.Tests;

public sealed class RemoteRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpFailureDoesNotEchoSensitiveBody(bool streaming)
    {
        var runtime = CreateRuntime(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StringContent("Bearer fixture-secret private prompt token=sensitive") })));
        await using var session = await runtime.CreateSessionAsync(CreateCapabilities());
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

    [Fact]
    public async Task InvokeAsync_UsesOpenAiCompatibleRouteAndModelId()
    {
        var handler = new StubHandler(async request =>
        {
            Assert.Equal("https://inference.example/v1/chat/completions", request.RequestUri?.ToString());
            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());
            var body = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            Assert.Equal("owner/model", json.RootElement.GetProperty("model").GetString());
            Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
            return Json("""{ "choices": [{ "message": { "content": "ok" } }] }""");
        });
        var runtime = CreateRuntime(handler);
        await using var session = await runtime.CreateSessionAsync(CreateCapabilities());
        using var payload = JsonDocument.Parse("""{ "messages": [{ "role": "user", "content": "hi" }] }""");

        var response = await session.InvokeAsync(new ModelRequest("chat", payload.RootElement.Clone()));

        Assert.Equal("remote", response.Runtime);
        Assert.Equal("ok", response.Output.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task InvokeStreamingAsync_ParsesServerSentEvents()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\ndata: [DONE]\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        }));
        var runtime = CreateRuntime(handler);
        await using var session = await runtime.CreateSessionAsync(CreateCapabilities());
        using var payload = JsonDocument.Parse("""{ "messages": [] }""");

        var events = new List<ModelStreamEvent>();
        await foreach (var item in session.InvokeStreamingAsync(
            new ModelRequest("chat", payload.RootElement.Clone(), Stream: true)))
        {
            events.Add(item);
        }

        Assert.Equal(2, events.Count);
        Assert.False(events[0].IsTerminal);
        Assert.True(events[1].IsTerminal);
    }

    private static ModelScopeRemoteRuntime CreateRuntime(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new RemoteRuntimeOptions
        {
            Endpoint = new Uri("https://inference.example/"),
            Token = "test-token",
        });

    private static ModelCapabilities CreateCapabilities() => new(
        string.Empty,
        "owner/model",
        "commit",
        "chat",
        [],
        [],
        [],
        false,
        []);

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
