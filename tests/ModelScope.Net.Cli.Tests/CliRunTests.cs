using System.Text.Json;
using ModelScope.Net;

namespace ModelScope.Net.Cli.Tests;

[CollectionDefinition("CliOutput", DisableParallelization = true)]
public sealed class CliOutputCollection;

[Collection("CliOutput")]
public sealed class CliRunTests
{
    private const string AddModelBase64 =
        "CAoSFE1vZGVsU2NvcGUuTmV0LlRlc3RzOlgKEAoBeAoBeRIDc3VtIgNBZGQSD2FkZC10d28tdmVjdG9yc1oPCgF4EgoKCAgBEgQKAggCWg8KAXkSCgoICAESBAoCCAJiEQoDc3VtEgoKCAgBEgQKAggCQgQKABAN";

    [Fact]
    public async Task Run_LocalDirectory_InvokesOnnxRuntime()
    {
        var directory = CreateOnnxDirectory();
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            var code = await global::Cli.RunAsync(
            [
                "run",
                directory,
                "--runtime",
                "onnx",
                "--task",
                "raw-onnx",
                "--payload",
                """{"inputs":{"x":[1.25,2.5],"y":[2.75,3.5]}}""",
            ]);

            Assert.Equal(0, code);
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal("onnx", document.RootElement.GetProperty("runtime").GetString());
            Assert.Equal(
                [4f, 6f],
                document.RootElement.GetProperty("output").GetProperty("outputs").GetProperty("sum")
                    .GetProperty("data").EnumerateArray().Select(item => item.GetSingle()).ToArray());
        }
        finally
        {
            Console.SetOut(original);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Run_LocalDirectory_RoutesWithoutExplicitRuntime()
    {
        var directory = CreateOnnxDirectory();
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            var code = await global::Cli.RunAsync(
            [
                "run",
                directory,
                "--payload",
                """{"inputs":{"x":[1.0,2.0],"y":[3.0,4.0]}}""",
            ]);

            Assert.Equal(0, code);
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal("onnx", document.RootElement.GetProperty("runtime").GetString());
            Assert.Equal(
                [4f, 6f],
                document.RootElement.GetProperty("output").GetProperty("outputs").GetProperty("sum")
                    .GetProperty("data").EnumerateArray().Select(item => item.GetSingle()).ToArray());
        }
        finally
        {
            Console.SetOut(original);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Run_OwnerNameWithoutLocalDirectory_RequiresRemoteToken()
    {
        var error = new StringWriter();
        var original = Console.Error;
        var previousApi = Environment.GetEnvironmentVariable("MODELSCOPE_API_TOKEN");
        var previousAccess = Environment.GetEnvironmentVariable("MODELSCOPE_ACCESS_TOKEN");
        Environment.SetEnvironmentVariable("MODELSCOPE_API_TOKEN", null);
        Environment.SetEnvironmentVariable("MODELSCOPE_ACCESS_TOKEN", null);
        Console.SetError(error);
        var credentials = Path.Combine(Path.GetTempPath(), $"modelscope-net-cli-creds-{Guid.NewGuid():N}");
        try
        {
            var code = await global::Cli.RunAsync(
            [
                "run",
                "Qwen/Demo",
                "--prompt",
                "hello",
                "--credentials",
                credentials,
            ]);

            Assert.Equal(1, code);
            Assert.Contains(nameof(ModelScopeErrorCode.AuthenticationRequired), error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(original);
            Environment.SetEnvironmentVariable("MODELSCOPE_API_TOKEN", previousApi);
            Environment.SetEnvironmentVariable("MODELSCOPE_ACCESS_TOKEN", previousAccess);
        }
    }

    [Fact]
    public async Task Run_LocalDirectory_UnknownRuntimeReturnsStructuredError()
    {
        var directory = CreateOnnxDirectory();
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try
        {
            var code = await global::Cli.RunAsync(
            [
                "run",
                directory,
                "--runtime",
                "gguf",
                "--prompt",
                "hello",
            ]);

            Assert.Equal(1, code);
            Assert.Contains(nameof(ModelScopeErrorCode.RuntimeNotInstalled), error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(original);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_LocalDirectory_FallsBackToPythonWhenOnnxCannotLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"modelscope-net-cli-python-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "model.onnx"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(directory, "model.safetensors"), [0]);
        var output = new StringWriter();
        var error = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var code = await global::Cli.RunAsync(
            [
                "run",
                directory,
                "--python-worker",
                "--python-backend",
                "echo",
                "--payload",
                """{"text":"hello"}""",
            ]);

            Assert.Equal(0, code);
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal("python", document.RootElement.GetProperty("runtime").GetString());
            Assert.Equal("hello", document.RootElement.GetProperty("output").GetProperty("payload").GetProperty("text").GetString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateOnnxDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"modelscope-net-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "model.onnx"), Convert.FromBase64String(AddModelBase64));
        return directory;
    }
}
