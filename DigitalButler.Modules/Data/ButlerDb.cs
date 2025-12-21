using Microsoft.Data.Sqlite;

namespace DigitalButler.Modules.Data;

public interface IButlerDb
{
    Task<SqliteConnection> OpenAsync(CancellationToken ct = default);
}

public sealed class SqliteButlerDb : IButlerDb
{
    private readonly string _connectionString;

    public SqliteButlerDb(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
