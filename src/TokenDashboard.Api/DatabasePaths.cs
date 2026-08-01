using Microsoft.Data.Sqlite;

namespace TokenDashboard.Api;

public static class DatabasePaths
{
    public static string DefaultDataDirectory => Path.Combine(
        LocalApplicationData,
        OperatingSystem.IsLinux() ? "token-dashboard" : "TokenDashboard",
        "data");

    public static string DefaultDatabasePath => Path.Combine(DefaultDataDirectory, "token-dashboard.db");

    public static string DefaultConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DefaultDatabasePath
    }.ToString();

    public static void EnsureDefaultDataDirectory() => Directory.CreateDirectory(DefaultDataDirectory);

    private static string LocalApplicationData
    {
        get
        {
            var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(path)
                ? throw new InvalidOperationException("The local application data directory could not be determined")
                : path;
        }
    }
}
