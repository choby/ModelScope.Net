using System.Security.Cryptography;
using System.Text;

namespace ModelScope.Net.Hub;

internal static class SafePath
{
    public static string CombineUnderRoot(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(part => part is "." or ".." || string.IsNullOrEmpty(part)))
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.InvalidRequest,
                $"Repository path '{relativePath}' is unsafe.");
        }

        var fullRoot = Path.GetFullPath(root);
        var result = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ModelScopeException(
                ModelScopeErrorCode.InvalidRequest,
                $"Repository path '{relativePath}' escapes the target directory.");
        }

        return result;
    }

    public static string SanitizeSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character =>
            character is '/' or '\\' || invalid.Contains(character) ? '_' : character).ToArray());
        return sanitized is "." or ".." ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant() : sanitized;
    }
}
