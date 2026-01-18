using Dapper;
using DigitalButler.Common;

namespace DigitalButler.Data.Repositories;

public sealed class ContextUpdateLogRepository
{
    private readonly IButlerDb _db;

    public ContextUpdateLogRepository(IButlerDb db)
    {
        _db = db;
    }

    public async Task<long> AddAsync(ContextUpdateLog log, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        const string sql = """
            INSERT INTO ContextUpdateLog (
                Timestamp, Source, Status, ItemsScanned, ItemsAdded, ItemsUpdated, ItemsUnchanged, DurationMs, Message, Details
            ) VALUES (
                @Timestamp, @Source, @Status, @ItemsScanned, @ItemsAdded, @ItemsUpdated, @ItemsUnchanged, @DurationMs, @Message, @Details
            );
            SELECT last_insert_rowid();
            """;

        var id = await conn.ExecuteScalarAsync<long>(sql, new
        {
            log.Timestamp,
            log.Source,
            log.Status,
            log.ItemsScanned,
            log.ItemsAdded,
            log.ItemsUpdated,
            log.ItemsUnchanged,
            log.DurationMs,
            log.Message,
            log.Details
        });

        log.Id = id;
        return id;
    }

    public async Task<List<ContextUpdateLog>> GetRecentAsync(int take = 100, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        const string sql = """
            SELECT Id, Timestamp, Source, Status, ItemsScanned, ItemsAdded, ItemsUpdated, ItemsUnchanged, DurationMs, Message, Details
            FROM ContextUpdateLog
            ORDER BY Timestamp DESC
            LIMIT @Take;
            """;

        var rows = await conn.QueryAsync<ContextUpdateLog>(sql, new { Take = take });
        return rows.ToList();
    }

    public async Task<List<ContextUpdateLog>> GetBySourceAsync(string source, int take = 50, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        const string sql = """
            SELECT Id, Timestamp, Source, Status, ItemsScanned, ItemsAdded, ItemsUpdated, ItemsUnchanged, DurationMs, Message, Details
            FROM ContextUpdateLog
            WHERE Source = @Source
            ORDER BY Timestamp DESC
            LIMIT @Take;
            """;

        var rows = await conn.QueryAsync<ContextUpdateLog>(sql, new { Source = source, Take = take });
        return rows.ToList();
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = "DELETE FROM ContextUpdateLog WHERE Timestamp < @Cutoff;";
        return await conn.ExecuteAsync(sql, new { Cutoff = cutoff });
    }
}
