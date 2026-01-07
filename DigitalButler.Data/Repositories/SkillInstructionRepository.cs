using Dapper;
using DigitalButler.Common;

namespace DigitalButler.Data.Repositories;

public sealed class SkillInstructionRepository
{
    private readonly IButlerDb _db;

    public SkillInstructionRepository(IButlerDb db)
    {
        _db = db;
    }

    public async Task<List<SkillInstruction>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, Skill, Content, CreatedAt, UpdatedAt
            FROM SkillInstructions
            ORDER BY Skill;
            """;

        var rows = await conn.QueryAsync<Row>(sql);
        return rows.Select(Map).ToList();
    }

    public async Task<Dictionary<ButlerSkill, string>> GetBySkillsAsync(IEnumerable<ButlerSkill> skills, CancellationToken ct = default)
    {
        var list = skills.Distinct().ToArray();
        if (list.Length == 0)
        {
            return new Dictionary<ButlerSkill, string>();
        }

        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, Skill, Content, CreatedAt, UpdatedAt
            FROM SkillInstructions
            WHERE Skill IN @Skills;
            """;

        var rows = await conn.QueryAsync<Row>(sql, new { Skills = list.Select(x => (int)x).ToArray() });
        return rows
            .Select(Map)
            .ToDictionary(x => x.Skill, x => x.Content ?? string.Empty);
    }

    public async Task ReplaceAllAsync(IEnumerable<SkillInstruction> items, CancellationToken ct = default)
    {
        var list = items.ToList();
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existingIds = (await conn.QueryAsync<Guid>("SELECT Id FROM SkillInstructions;", transaction: tx)).ToHashSet();
        var newIds = list.Select(x => x.Id).Where(x => x != Guid.Empty).ToHashSet();

        foreach (var id in existingIds.Except(newIds))
        {
            await conn.ExecuteAsync("DELETE FROM SkillInstructions WHERE Id = @Id;", new { Id = id }, tx);
        }

        foreach (var item in list)
        {
            var id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;
            var now = DateTimeOffset.UtcNow;
            var created = item.CreatedAt == default ? now : item.CreatedAt;
            var updated = now;

            const string upsert = """
                INSERT INTO SkillInstructions (Id, Skill, Content, CreatedAt, UpdatedAt)
                VALUES (@Id, @Skill, @Content, @CreatedAt, @UpdatedAt)
                ON CONFLICT(Id) DO UPDATE SET
                    Skill = excluded.Skill,
                    Content = excluded.Content,
                    UpdatedAt = excluded.UpdatedAt;
                """;

            await conn.ExecuteAsync(upsert, new
            {
                Id = id,
                Skill = (int)item.Skill,
                Content = item.Content ?? string.Empty,
                CreatedAt = created,
                UpdatedAt = updated
            }, tx);
        }

        await tx.CommitAsync(ct);
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
        public int Skill { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private static SkillInstruction Map(Row row) => new()
    {
        Id = row.Id,
        Skill = (ButlerSkill)row.Skill,
        Content = row.Content,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt
    };
}
