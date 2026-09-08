using System.Text.Json;
using ModelScope.Net.Runtime.Python;

namespace ModelScope.Net.Runtime.Tests;

public class SpeechRecognitionContractsTests
{
    private static byte[] Wave(int rate = 16000, short channels = 1, short bits = 16, int frames = 160)
    {
        var data = new byte[44 + frames * channels * bits / 8];
        "RIFF"u8.CopyTo(data); BitConverter.GetBytes(data.Length - 8).CopyTo(data, 4); "WAVEfmt "u8.CopyTo(data.AsSpan(8));
        BitConverter.GetBytes(16).CopyTo(data, 16); BitConverter.GetBytes((short)1).CopyTo(data, 20);
        BitConverter.GetBytes(channels).CopyTo(data, 22); BitConverter.GetBytes(rate).CopyTo(data, 24);
        BitConverter.GetBytes(rate * channels * bits / 8).CopyTo(data, 28); BitConverter.GetBytes((short)(channels * bits / 8)).CopyTo(data, 32);
        BitConverter.GetBytes(bits).CopyTo(data, 34); "data"u8.CopyTo(data.AsSpan(36)); BitConverter.GetBytes(data.Length - 44).CopyTo(data, 40);
        return data;
    }
    private static ModelResponse Response(object value) => new(JsonSerializer.SerializeToElement(value), "model", "commit", "python", TimeSpan.Zero);

    [Fact]
    public void MapsSupportedInlineWaveRequest()
    {
        var wave = Wave(); var request = new SpeechRecognitionRequest(wave).ToModelRequest();
        Assert.Equal(SpeechRecognitionRequest.TaskName, request.Task);
        Assert.Equal("audio/wav", request.Payload.GetProperty("audio").GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(wave), request.Payload.GetProperty("audio").GetProperty("data").GetString());
        Assert.Null(request.Parameters);
    }

    [Fact]
    public void RejectsMalformedOrUnsupportedWave()
    {
        foreach (var wave in new[] { Array.Empty<byte>(), new byte[44], Wave(8000), Wave(channels: 2), Wave(bits: 8) })
            Assert.Equal(ModelScopeErrorCode.InvalidRequest,
                Assert.Throws<ModelScopeException>(() => new SpeechRecognitionRequest(wave).ToModelRequest()).Code);
    }

    [Fact]
    public void ParsesTextAndPreservesResponse()
    {
        var response = Response(new { text = "每一天都要快乐喔" });
        var result = SpeechRecognitionResult.FromResponse(response);
        Assert.Equal("每一天都要快乐喔", result.Text); Assert.Same(response, result.Response);
    }

    [Fact]
    public void RejectsMalformedOrOversizedOutput()
    {
        foreach (var value in new object[] { new { }, new { text = 1 }, new { text = new string('x', 65537) } })
            Assert.Equal(ModelScopeErrorCode.InferenceFailed,
                Assert.Throws<ModelScopeException>(() => SpeechRecognitionResult.FromResponse(Response(value))).Code);
    }
}
