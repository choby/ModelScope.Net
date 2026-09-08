using System.Text.Json;
using ModelScope.Net;

namespace ModelScope.Net.Hub.Tests;

public sealed class ModelIdTests
{
    [Theory]
    [InlineData("Qwen/Qwen2.5-0.5B-Instruct", "Qwen", "Qwen2.5-0.5B-Instruct")]
    [InlineData("owner/model", "owner", "model")]
    public void Parse_ValidId_ReturnsParts(string value, string owner, string name)
    {
        var id = ModelId.Parse(value);

        Assert.Equal(owner, id.Owner);
        Assert.Equal(name, id.Name);
        Assert.Equal(value, id.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("model")]
    [InlineData("owner/model/extra")]
    [InlineData("../model")]
    [InlineData("owner/..")]
    public void Parse_InvalidId_Throws(string value)
    {
        Assert.Throws<FormatException>(() => ModelId.Parse(value));
    }

    [Fact]
    public void JsonRoundTrip_PreservesOwnerAndName()
    {
        var original = ModelId.Parse("owner/model");
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<ModelId>(json);

        Assert.Equal(original, restored);
        Assert.Contains("\"owner\":\"owner\"", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"model\"", json, StringComparison.Ordinal);
    }
}
