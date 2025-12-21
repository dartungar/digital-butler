using Dapper;
using DigitalButler.Modules.Data;

namespace DigitalButler.Modules.Repositories;

public sealed class GoogleCalendarFeedRepository
{
    private readonly IButlerDb _db;

    public GoogleCalendarFeedRepository(IButlerDb db)
    {
        _db = db;
    }

    public async Task<List<GoogleCalendarFeed>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, Name, Url, Enabled, CreatedAt, UpdatedAt
            FROM GoogleCalendarFeeds
            ORDER BY Name;
            """;

        var rows = await conn.QueryAsync<GoogleCalendarFeed>(sql);
        return rows.ToList();
    }

    public async Task<List<GoogleCalendarFeed>> GetEnabledAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, Name, Url, Enabled, CreatedAt, UpdatedAt
            FROM GoogleCalendarFeeds
            WHERE Enabled = 1
            ORDER BY Name;
            """;

        var rows = await conn.QueryAsync<GoogleCalendarFeed>(sql);
        return rows.ToList();
    }

    public async Task ReplaceAllAsync(IEnumerable<GoogleCalendarFeed> feeds, CancellationToken ct = default)
    {
        var cleaned = feeds
            .Where(f => !string.IsNullOrWhiteSpace(f.Url))
            .ToList();

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existingIds = (await conn.QueryAsync<Guid>("SELECT Id FROM GoogleCalendarFeeds;", transaction: tx)).ToHashSet();
        var keepIds = cleaned.Select(x => x.Id).ToHashSet();

        foreach (var id in existingIds)
        {
            if (!keepIds.Contains(id))
            {
                await conn.ExecuteAsync("DELETE FROM GoogleCalendarFeeds WHERE Id = @Id;", new { Id = id }, tx);
            }
        }

        const string upsertSql = """
            INSERT INTO GoogleCalendarFeeds (Id, Name, Url, Enabled, CreatedAt, UpdatedAt)
            VALUES (@Id, @Name, @Url, @Enabled, @CreatedAt, @UpdatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Url = excluded.Url,
                Enabled = excluded.Enabled,
                UpdatedAt = excluded.UpdatedAt;
            """;

        foreach (var feed in cleaned)
        {
            if (feed.Id == Guid.Empty) feed.Id = Guid.NewGuid();
            if (feed.CreatedAt == default) feed.CreatedAt = DateTimeOffset.UtcNow;
            feed.UpdatedAt = DateTimeOffset.UtcNow;

            await conn.ExecuteAsync(upsertSql, new
            {
                feed.Id,
                feed.Name,
                feed.Url,
                Enabled = feed.Enabled ? 1 : 0,
                feed.CreatedAt,
                feed.UpdatedAt
            }, tx);
        }

        await tx.CommitAsync(ct);
    }
}
