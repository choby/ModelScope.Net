using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModelScope.Net;

/// <summary>Identifies a repository on ModelScope as owner/name.</summary>
[JsonConverter(typeof(ModelIdJsonConverter))]
public readonly record struct ModelId
{
    public ModelId(string owner, string name)
    {
        Owner = ValidatePart(owner, nameof(owner));
        Name = ValidatePart(name, nameof(name));
    }

    public string Owner { get; }

    public string Name { get; }

    public static ModelId Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("Model ID must use the form 'owner/name'.");
        }

        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new FormatException("Model ID must use the form 'owner/name'.");
        }

        try
        {
            return new ModelId(parts[0], parts[1]);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("Model ID contains an invalid owner or name.", exception);
        }
    }

    public static bool TryParse(string? value, out ModelId modelId)
    {
        try
        {
            modelId = Parse(value ?? string.Empty);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            modelId = default;
            return false;
        }
    }

    public override string ToString() => $"{Owner}/{Name}";

    private static string ValidatePart(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value is "." or ".." ||
            value.Contains('\\', StringComparison.Ordinal) ||
            value.Contains('/', StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException("Model ID parts cannot contain path separators, traversal, or control characters.", parameterName);
        }

        return value;
    }
}

internal sealed class ModelIdJsonConverter : JsonConverter<ModelId>
{
    public override ModelId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return ModelId.Parse(reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Model ID must be a string or an object with owner and name.");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var owner = GetProperty(document.RootElement, "owner") ?? GetProperty(document.RootElement, "Owner");
        var name = GetProperty(document.RootElement, "name") ?? GetProperty(document.RootElement, "Name");
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
        {
            throw new JsonException("Model ID object must contain owner and name.");
        }

        return new ModelId(owner, name);
    }

    public override void Write(Utf8JsonWriter writer, ModelId value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("owner", value.Owner);
        writer.WriteString("name", value.Name);
        writer.WriteEndObject();
    }

    private static string? GetProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}
