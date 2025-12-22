using Dapper;
using DigitalButler.Modules.Data;

namespace DigitalButler.Modules.Repositories;

public sealed class AppSettingsRepository
{
    private readonly IButlerDb _db;

    public AppSettingsRepository(IButlerDb db)
    {
        _db = db;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        const string sql = """
            SELECT Value
            FROM AppSettings
            WHERE Key = @Key;
            """;

        return await conn.QuerySingleOrDefaultAsync<string?>(sql, new { Key = key });
    }

    public async Task UpsertAsync(string key, string value, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        const string sql = """
            INSERT INTO AppSettings (Key, Value, UpdatedAt)
            VALUES (@Key, @Value, @UpdatedAt)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAt = excluded.UpdatedAt;
            """;

        await conn.ExecuteAsync(sql, new { Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
    }
}
