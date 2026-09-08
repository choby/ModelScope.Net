namespace ModelScope.Net.Hub;

/// <summary>Stores a ModelScope API token in a user-only local file.</summary>
public sealed class ModelScopeCredentialStore
{
    public ModelScopeCredentialStore(string? credentialPath = null)
    {
        CredentialPath = Path.GetFullPath(credentialPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".modelscope-net",
            "credentials"));
    }

    public string CredentialPath { get; }

    public string? LoadToken()
    {
        if (!File.Exists(CredentialPath))
        {
            return null;
        }

        var token = File.ReadAllText(CredentialPath).Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public void SaveToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        token = token.Trim();
        if (token.Any(char.IsControl))
        {
            throw new ArgumentException("Token cannot contain control characters.", nameof(token));
        }

        var directory = Path.GetDirectoryName(CredentialPath)
            ?? throw new InvalidOperationException("Credential path has no parent directory.");
        Directory.CreateDirectory(directory);
        SetDirectoryPermissions(directory);
        var temporary = $"{CredentialPath}.tmp.{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporary, token);
            SetFilePermissions(temporary);
            File.Move(temporary, CredentialPath, true);
            SetFilePermissions(CredentialPath);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public bool DeleteToken()
    {
        if (!File.Exists(CredentialPath))
        {
            return false;
        }

        File.Delete(CredentialPath);
        return true;
    }

    private static void SetDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
