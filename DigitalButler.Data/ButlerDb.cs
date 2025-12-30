using Microsoft.Data.Sqlite;

namespace DigitalButler.Data;

public interface IButlerDb
{
    Task<SqliteConnection> OpenAsync(CancellationToken ct = default);
}

public sealed class SqliteButlerDb : IButlerDb
{
    private readonly string _connectionString;

    public SqliteButlerDb(string connectionString)
    {
        // Enable connection pooling for better performance
        _connectionString = connectionString.Contains("Pooling=", StringComparison.OrdinalIgnoreCase)
            ? connectionString
            : connectionString + ";Pooling=True";
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
