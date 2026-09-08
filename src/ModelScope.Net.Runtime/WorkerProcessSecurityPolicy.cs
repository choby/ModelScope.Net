using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ModelScope.Net.Runtime;

public sealed class WorkerProcessSecurityOptions
{
    private static readonly string[] DefaultInheritedVariables =
    [
        "PATH",
        "LANG",
        "LC_ALL",
        "LC_CTYPE",
        "TMPDIR",
        "TMP",
        "TEMP",
        "SYSTEMROOT",
        "WINDIR",
        "COMSPEC",
        "PATHEXT",
    ];

    public WorkerProcessSecurityOptions()
    {
        foreach (var variable in DefaultInheritedVariables)
        {
            AllowedInheritedEnvironmentVariables.Add(variable);
        }
    }

    public bool InheritParentEnvironment { get; set; }

    public bool AllowSensitiveEnvironmentVariables { get; set; }

    public bool CaptureDiagnostics { get; set; }

    public int MaxDiagnosticCharacters { get; set; } = 4096;

    public ISet<string> AllowedInheritedEnvironmentVariables { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public static partial class WorkerProcessSecurityPolicy
{
    private static readonly string[] SensitiveNameParts =
    [
        "TOKEN",
        "SECRET",
        "PASSWORD",
        "PASSWD",
        "API_KEY",
        "APIKEY",
        "AUTHORIZATION",
        "COOKIE",
        "CREDENTIAL",
        "PRIVATE_KEY",
    ];

    public static void ApplyEnvironment(
        ProcessStartInfo startInfo,
        IEnumerable<KeyValuePair<string, string>> configuredEnvironment,
        WorkerProcessSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(configuredEnvironment);
        Validate(options);

        if (!options.InheritParentEnvironment)
        {
            startInfo.Environment.Clear();
            foreach (var name in options.AllowedInheritedEnvironmentVariables)
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (value is not null) startInfo.Environment[name] = value;
            }
        }

        foreach (var variable in configuredEnvironment)
        {
            ValidateEnvironmentVariable(variable.Key, variable.Value, options);
            startInfo.Environment[variable.Key] = variable.Value;
        }
    }

    public static string SanitizeDiagnostic(string value, WorkerProcessSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        Validate(options);
        var sanitized = ControlCharacterRegex().Replace(value, "?");
        sanitized = BearerRegex().Replace(sanitized, "Bearer <redacted>");
        sanitized = NamedSecretRegex().Replace(sanitized, match => $"{match.Groups[1].Value}=<redacted>");
        sanitized = ModelScopeTokenRegex().Replace(sanitized, "ms-<redacted>");
        return sanitized.Length <= options.MaxDiagnosticCharacters
            ? sanitized
            : sanitized[..options.MaxDiagnosticCharacters] + "…";
    }

    public static bool IsSensitiveEnvironmentVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim().ToUpperInvariant().Replace('-', '_');
        return SensitiveNameParts.Any(normalized.Contains);
    }

    public static void Validate(WorkerProcessSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDiagnosticCharacters, 128);
        foreach (var name in options.AllowedInheritedEnvironmentVariables)
        {
            ValidateEnvironmentVariableName(name);
        }
    }

    private static void ValidateEnvironmentVariable(
        string name,
        string value,
        WorkerProcessSecurityOptions options)
    {
        ValidateEnvironmentVariableName(name);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0'))
        {
            throw new ArgumentException("Worker environment variable values cannot contain null characters.", nameof(value));
        }

        if (!options.AllowSensitiveEnvironmentVariables && IsSensitiveEnvironmentVariable(name))
        {
            throw new ArgumentException(
                $"Worker environment variable '{name}' is classified as sensitive and cannot be forwarded by default.",
                nameof(name));
        }
    }

    private static void ValidateEnvironmentVariableName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('=') || name.Contains('\0'))
        {
            throw new ArgumentException("Worker environment variable names contain invalid characters.", nameof(name));
        }
    }

    [GeneratedRegex("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F\\x7F]")]
    private static partial Regex ControlCharacterRegex();

    [GeneratedRegex("(?i)Bearer\\s+[^\\s,;]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?i)\\b(authorization|api[-_]?key|token|secret|password|passwd|cookie|credential)\\b\\s*[:=]\\s*[^\\s,;]+")]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex("(?i)ms-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex ModelScopeTokenRegex();
}
