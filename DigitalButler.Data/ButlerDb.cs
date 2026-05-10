using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace DigitalButler.Data;

public interface IButlerDb
{
    Task<SqliteConnection> OpenAsync(CancellationToken ct = default);
}

public sealed class SqliteButlerDb : IButlerDb
{
    private readonly string _connectionString;
    private static readonly Lazy<string?> VecExtensionPath = new(FindVecExtension);

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

        await ConfigureConnectionAsync(connection, ct);

        // Load sqlite-vec extension if available
        if (VecExtensionPath.Value is { } path)
        {
            connection.LoadExtension(path);
        }

        return connection;
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON;", ct);
        await ExecutePragmaAsync(connection, "PRAGMA busy_timeout = 5000;", ct);

        try
        {
            await ExecutePragmaAsync(connection, "PRAGMA journal_mode = WAL;", ct);
        }
        catch (SqliteException)
        {
            // WAL is unavailable for some SQLite targets such as in-memory databases.
        }
    }

    private static async Task ExecutePragmaAsync(SqliteConnection connection, string commandText, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string? FindVecExtension()
    {
        // The sqlite-vec NuGet package places native libraries in runtimes/<rid>/native/
        var rid = RuntimeInformation.RuntimeIdentifier;

        // Common RID mappings for sqlite-vec
        var possibleRids = new List<string> { rid };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            possibleRids.Add("linux-x64");
            possibleRids.Add("linux-arm64");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            possibleRids.Add("win-x64");
            possibleRids.Add("win-arm64");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            possibleRids.Add("osx-x64");
            possibleRids.Add("osx-arm64");
        }

        var baseDir = AppContext.BaseDirectory;
        var extensionName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "vec0.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "vec0.dylib"
            : "vec0.so";

        foreach (var r in possibleRids.Distinct())
        {
            var path = Path.Combine(baseDir, "runtimes", r, "native", extensionName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Try direct path (might be copied to output)
        var directPath = Path.Combine(baseDir, extensionName);
        if (File.Exists(directPath))
        {
            return directPath;
        }

        return null;
    }
}
