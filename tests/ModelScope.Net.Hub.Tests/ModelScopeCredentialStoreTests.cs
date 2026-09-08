using ModelScope.Net.Hub;

namespace ModelScope.Net.Hub.Tests;

public sealed class ModelScopeCredentialStoreTests
{
    [Fact]
    public void SaveLoadDelete_RoundTripsTokenWithoutLeavingTemporaryFile()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "nested", "credentials");
            var store = new ModelScopeCredentialStore(path);

            store.SaveToken("test-secret-token");

            Assert.Equal("test-secret-token", store.LoadToken());
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp.*"));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));
            }

            Assert.True(store.DeleteToken());
            Assert.Null(store.LoadToken());
            Assert.False(store.DeleteToken());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"modelscope-net-credentials-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
