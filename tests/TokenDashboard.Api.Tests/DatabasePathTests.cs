using Xunit;

namespace TokenDashboard.Api.Tests;

public sealed class DatabasePathTests
{
    [Fact]
    public void DefaultDatabasePathUsesLocalApplicationDataAndPlatformProductDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var productDirectory = OperatingSystem.IsLinux() ? "token-dashboard" : "TokenDashboard";
        var expectedDataDirectory = Path.Combine(localApplicationData, productDirectory, "data");

        Assert.Equal(expectedDataDirectory, DatabasePaths.DefaultDataDirectory);
        Assert.Equal(Path.Combine(expectedDataDirectory, "token-dashboard.db"), DatabasePaths.DefaultDatabasePath);
        Assert.Contains(DatabasePaths.DefaultDatabasePath, DatabasePaths.DefaultConnectionString, StringComparison.Ordinal);
    }
}
