using Dapper;
using DigitalButler.Common;

namespace DigitalButler.Data.Repositories;

public sealed class InstructionRepository
{
    private readonly IButlerDb _db;

    public InstructionRepository(IButlerDb db)
    {
        _db = db;
    }

    public async Task<List<Instruction>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, Source, Content, CreatedAt, UpdatedAt
            FROM Instructions
            ORDER BY Source;
            """;

        var rows = await conn.QueryAsync<InstructionRow>(sql);
        return rows.Select(Map).ToList();
    }

    public async Task<string> GetMergedAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Content
            FROM Instructions
            ORDER BY Source;
            """;

        var parts = await conn.QueryAsync<string>(sql);
        return string.Join("\n", parts);
    }

    public async Task<Dictionary<ContextSource, string>> GetBySourcesAsync(IEnumerable<ContextSource> sources, CancellationToken ct = default)
    {
        var sourceList = sources.Distinct().Select(x => (int)x).ToArray();
        if (sourceList.Length == 0)
        {
            return new Dictionary<ContextSource, string>();
        }

        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Source, Content
            FROM Instructions
            WHERE Source IN @Sources
            ORDER BY Source;
            """;

        var rows = await conn.QueryAsync<(int Source, string Content)>(sql, new { Sources = sourceList });
        var dict = new Dictionary<ContextSource, string>();
        foreach (var row in rows)
        {
            dict[(ContextSource)row.Source] = row.Content ?? string.Empty;
        }
        return dict;
    }

    public async Task ReplaceAllAsync(IEnumerable<Instruction> items, CancellationToken ct = default)
    {
        var cleaned = items.ToList();
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existingIds = (await conn.QueryAsync<Guid>("SELECT Id FROM Instructions;", transaction: tx)).ToHashSet();
        var keepIds = cleaned.Select(x => x.Id).ToHashSet();

        foreach (var id in existingIds)
        {
            if (!keepIds.Contains(id))
            {
                await conn.ExecuteAsync("DELETE FROM Instructions WHERE Id = @Id;", new { Id = id }, tx);
            }
        }

        const string upsertSql = """
            INSERT INTO Instructions (Id, Source, Content, CreatedAt, UpdatedAt)
            VALUES (@Id, @Source, @Content, @CreatedAt, @UpdatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                Source = excluded.Source,
                Content = excluded.Content,
                UpdatedAt = excluded.UpdatedAt;
            """;

        foreach (var item in cleaned)
        {
            if (item.Id == Guid.Empty) item.Id = Guid.NewGuid();
            if (item.CreatedAt == default) item.CreatedAt = DateTimeOffset.UtcNow;
            item.UpdatedAt = DateTimeOffset.UtcNow;

            await conn.ExecuteAsync(upsertSql, new
            {
                item.Id,
                Source = (int)item.Source,
                item.Content,
                item.CreatedAt,
                item.UpdatedAt
            }, tx);
        }

        await tx.CommitAsync(ct);
    }

    private sealed class InstructionRow
    {
        public Guid Id { get; set; }
        public int Source { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private static Instruction Map(InstructionRow row) => new()
    {
        Id = row.Id,
        Source = (ContextSource)row.Source,
        Content = row.Content,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt
    };
}
