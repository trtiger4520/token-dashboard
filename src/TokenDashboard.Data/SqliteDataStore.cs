using Microsoft.Data.Sqlite;

namespace TokenDashboard.Data;

public sealed class SqliteDataStore : IDisposable, IAsyncDisposable
{
    public SqliteDataStore(string connectionString)
    {
        Connection = new SqliteConnection(connectionString);
        Connection.Open();
        using (var pragma = Connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
            pragma.ExecuteNonQuery();
        }
        SchemaMigrator.Migrate(Connection);
    }

    public SqliteConnection Connection { get; }

    public void Dispose()
    {
        Connection.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Connection.Dispose();
        return ValueTask.CompletedTask;
    }
}
