using Microsoft.Data.Sqlite;

namespace TokenDashboard.Data;

public sealed class SqliteDataStore : IDisposable, IAsyncDisposable
{
    public SqliteDataStore(string connectionString)
    {
        Connection = new SqliteConnection(connectionString);
        Connection.Open();
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
