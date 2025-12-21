using Dapper;
using DigitalButler.Modules.Data;

namespace DigitalButler.Modules.Repositories;

public sealed class ContextRepository
{
    private readonly IButlerDb _db;

    public ContextRepository(IButlerDb db)
    {
        _db = db;
    }

    public async Task<ContextItem> InsertAsync(ContextItem item, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        const string sql = """
            INSERT INTO ContextItems (
                Id, Source, Title, Body, RelevantDate, IsTimeless, CreatedAt, UpdatedAt, ExternalId, Category, Summary
            ) VALUES (
                @Id, @Source, @Title, @Body, @RelevantDate, @IsTimeless, @CreatedAt, @UpdatedAt, @ExternalId, @Category, @Summary
            );
            """;

        await conn.ExecuteAsync(sql, new
        {
            item.Id,
            Source = (int)item.Source,
            item.Title,
            item.Body,
            item.RelevantDate,
            IsTimeless = item.IsTimeless ? 1 : 0,
            item.CreatedAt,
            item.UpdatedAt,
            item.ExternalId,
            item.Category,
            item.Summary
        });

        return item;
    }

    public async Task<List<ContextItem>> GetRecentAsync(int take = 200, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, Source, Title, Body, RelevantDate, IsTimeless, CreatedAt, UpdatedAt, ExternalId, Category, Summary
            FROM ContextItems
            ORDER BY UpdatedAt DESC
            LIMIT @Take;
            """;

        var rows = await conn.QueryAsync<ContextItemRow>(sql, new { Take = take });
        return rows.Select(Map).ToList();
    }

    public async Task<int> UpdateAsync(ContextItem item, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        const string sql = """
            UPDATE ContextItems
            SET Title = @Title,
                Body = @Body,
                RelevantDate = @RelevantDate,
                IsTimeless = @IsTimeless,
                UpdatedAt = @UpdatedAt,
                Category = @Category,
                Summary = @Summary
            WHERE Id = @Id;
            """;

        return await conn.ExecuteAsync(sql, new
        {
            item.Id,
            item.Title,
            item.Body,
            item.RelevantDate,
            IsTimeless = item.IsTimeless ? 1 : 0,
            item.UpdatedAt,
            item.Category,
            item.Summary
        });
    }

    public async Task<int> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = "DELETE FROM ContextItems WHERE Id = @Id;";
        return await conn.ExecuteAsync(sql, new { Id = id });
    }

    public async Task<List<ContextItem>> GetRelevantAsync(int daysBack = 7, int take = 200, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-daysBack);

        const string sql = """
            SELECT Id, Source, Title, Body, RelevantDate, IsTimeless, CreatedAt, UpdatedAt, ExternalId, Category, Summary
            FROM ContextItems
            WHERE IsTimeless = 1 OR RelevantDate >= @Cutoff
            ORDER BY UpdatedAt DESC
            LIMIT @Take;
            """;

        var rows = await conn.QueryAsync<ContextItemRow>(sql, new { Cutoff = cutoff, Take = take });
        return rows.Select(Map).ToList();
    }

    public async Task<int> UpsertByExternalIdAsync(IEnumerable<ContextItem> items, CancellationToken ct = default)
    {
        var list = items
            .Where(i => i.Source != ContextSource.Personal)
            .Where(i => !string.IsNullOrWhiteSpace(i.ExternalId))
            .ToList();

        if (list.Count == 0)
        {
            return 0;
        }

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var affected = 0;
        foreach (var item in list)
        {
            // Update existing rows (could be multiple if duplicates exist).
            const string updateSql = """
                UPDATE ContextItems
                SET Title = @Title,
                    Body = @Body,
                    RelevantDate = @RelevantDate,
                    IsTimeless = @IsTimeless,
                    UpdatedAt = @UpdatedAt,
                    Category = @Category,
                    Summary = @Summary
                WHERE Source = @Source AND ExternalId = @ExternalId;
                """;

            var updated = await conn.ExecuteAsync(updateSql, new
            {
                Source = (int)item.Source,
                item.ExternalId,
                item.Title,
                item.Body,
                item.RelevantDate,
                IsTimeless = item.IsTimeless ? 1 : 0,
                item.UpdatedAt,
                item.Category,
                item.Summary
            }, tx);

            if (updated == 0)
            {
                if (item.Id == Guid.Empty)
                {
                    item.Id = Guid.NewGuid();
                }

                const string insertSql = """
                    INSERT INTO ContextItems (
                        Id, Source, Title, Body, RelevantDate, IsTimeless, CreatedAt, UpdatedAt, ExternalId, Category, Summary
                    ) VALUES (
                        @Id, @Source, @Title, @Body, @RelevantDate, @IsTimeless, @CreatedAt, @UpdatedAt, @ExternalId, @Category, @Summary
                    );
                    """;

                await conn.ExecuteAsync(insertSql, new
                {
                    item.Id,
                    Source = (int)item.Source,
                    item.Title,
                    item.Body,
                    item.RelevantDate,
                    IsTimeless = item.IsTimeless ? 1 : 0,
                    item.CreatedAt,
                    item.UpdatedAt,
                    item.ExternalId,
                    item.Category,
                    item.Summary
                }, tx);
                affected += 1;
            }
            else
            {
                affected += updated;
            }
        }

        await tx.CommitAsync(ct);
        return affected;
    }

    private sealed class ContextItemRow
    {
        public Guid Id { get; set; }
        public int Source { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTimeOffset? RelevantDate { get; set; }
        public long IsTimeless { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? ExternalId { get; set; }
        public string? Category { get; set; }
        public string? Summary { get; set; }
    }

    private static ContextItem Map(ContextItemRow row) => new()
    {
        Id = row.Id,
        Source = (ContextSource)row.Source,
        Title = row.Title,
        Body = row.Body,
        RelevantDate = row.RelevantDate,
        IsTimeless = row.IsTimeless != 0,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        ExternalId = row.ExternalId,
        Category = row.Category,
        Summary = row.Summary
    };
}
