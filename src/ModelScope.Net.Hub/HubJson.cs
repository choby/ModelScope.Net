using System.Text.Json;

namespace ModelScope.Net.Hub;

internal static class HubJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static JsonElement UnwrapData(JsonDocument document)
    {
        var root = document.RootElement;
        var hasSuccess = TryGet(root, "Success", out var success);
        var hasCode = TryGet(root, "Code", out var codeElement);
        var code = 200;
        if (hasSuccess && success.ValueKind is not JsonValueKind.True and not JsonValueKind.False ||
            hasCode && !int.TryParse(codeElement.ToString(), out code))
            throw new ModelScopeException(ModelScopeErrorCode.RemoteApiUnavailable, "Invalid ModelScope API response status.");
        if (hasSuccess && success.ValueKind == JsonValueKind.False || hasCode && code != 200)
        {
            var error = code switch
            {
                401 or 403 => ModelScopeErrorCode.AuthenticationRequired,
                404 => ModelScopeErrorCode.ModelNotFound,
                429 => ModelScopeErrorCode.RemoteApiRateLimited,
                _ => ModelScopeErrorCode.RemoteApiUnavailable
            };
            // Upstream messages can echo tokens, signed URLs and private request details.
            throw new ModelScopeException(error, "ModelScope API reported an unsuccessful response.", code == 429 || code >= 500);
        }

        return TryGet(root, "Data", out var data) ? data : root;
    }

    public static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public static string? GetString(JsonElement element, string name)
    {
        return TryGet(element, name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;
    }

    public static long GetInt64(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : long.TryParse(value.ToString(), out number) ? number : 0;
    }

    public static IReadOnlyList<string> GetStringList(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(item.GetString()!);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var text = GetString(item, "Name") ?? GetString(item, "name");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text);
                }
            }
        }

        return result;
    }
}
