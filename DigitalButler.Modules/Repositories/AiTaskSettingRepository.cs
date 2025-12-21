using Dapper;
using DigitalButler.Modules.Data;

namespace DigitalButler.Modules.Repositories;

public sealed class AiTaskSettingRepository
{
    private readonly IButlerDb _db;

    public AiTaskSettingRepository(IButlerDb db)
    {
        _db = db;
    }

    public async Task<List<AiTaskSetting>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, TaskName, ProviderUrl, Model, ApiKey, UpdatedAt
            FROM AiTaskSettings
            ORDER BY TaskName;
            """;

        var rows = await conn.QueryAsync<AiTaskSetting>(sql);
        return rows.ToList();
    }

    public async Task<AiTaskSetting?> GetByTaskNameAsync(string taskName, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT Id, TaskName, ProviderUrl, Model, ApiKey, UpdatedAt
            FROM AiTaskSettings
            WHERE TaskName = @TaskName
            LIMIT 1;
            """;

        return await conn.QueryFirstOrDefaultAsync<AiTaskSetting>(sql, new { TaskName = taskName });
    }

    public async Task UpsertAsync(AiTaskSetting item, CancellationToken ct = default)
    {
        if (item.Id == Guid.Empty) item.Id = Guid.NewGuid();
        if (string.IsNullOrWhiteSpace(item.TaskName))
        {
            throw new ArgumentException("TaskName is required", nameof(item));
        }

        item.UpdatedAt = DateTimeOffset.UtcNow;

        await using var conn = await _db.OpenAsync(ct);
        const string upsertSql = """
            INSERT INTO AiTaskSettings (Id, TaskName, ProviderUrl, Model, ApiKey, UpdatedAt)
            VALUES (@Id, @TaskName, @ProviderUrl, @Model, @ApiKey, @UpdatedAt)
            ON CONFLICT(TaskName) DO UPDATE SET
                ProviderUrl = excluded.ProviderUrl,
                Model = excluded.Model,
                ApiKey = excluded.ApiKey,
                UpdatedAt = excluded.UpdatedAt;
            """;

        await conn.ExecuteAsync(upsertSql, item);
    }

    public async Task UpsertManyAsync(IEnumerable<AiTaskSetting> items, CancellationToken ct = default)
    {
        var list = items.Where(x => !string.IsNullOrWhiteSpace(x.TaskName)).ToList();
        if (list.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var item in list)
        {
            if (item.Id == Guid.Empty) item.Id = Guid.NewGuid();
            item.UpdatedAt = now;
        }

        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            INSERT INTO AiTaskSettings (Id, TaskName, ProviderUrl, Model, ApiKey, UpdatedAt)
            VALUES (@Id, @TaskName, @ProviderUrl, @Model, @ApiKey, @UpdatedAt)
            ON CONFLICT(TaskName) DO UPDATE SET
                ProviderUrl = excluded.ProviderUrl,
                Model = excluded.Model,
                ApiKey = excluded.ApiKey,
                UpdatedAt = excluded.UpdatedAt;
            """;

        await conn.ExecuteAsync(sql, list);
    }

    public async Task ReplaceAllAsync(IEnumerable<AiTaskSetting> items, CancellationToken ct = default)
    {
        var cleaned = items.Where(x => !string.IsNullOrWhiteSpace(x.TaskName)).ToList();

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existingIds = (await conn.QueryAsync<Guid>("SELECT Id FROM AiTaskSettings;", transaction: tx)).ToHashSet();
        var keepIds = cleaned.Select(x => x.Id).ToHashSet();

        foreach (var id in existingIds)
        {
            if (!keepIds.Contains(id))
            {
                await conn.ExecuteAsync("DELETE FROM AiTaskSettings WHERE Id = @Id;", new { Id = id }, tx);
            }
        }

        const string upsertSql = """
            INSERT INTO AiTaskSettings (Id, TaskName, ProviderUrl, Model, ApiKey, UpdatedAt)
            VALUES (@Id, @TaskName, @ProviderUrl, @Model, @ApiKey, @UpdatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                TaskName = excluded.TaskName,
                ProviderUrl = excluded.ProviderUrl,
                Model = excluded.Model,
                ApiKey = excluded.ApiKey,
                UpdatedAt = excluded.UpdatedAt;
            """;

        foreach (var item in cleaned)
        {
            if (item.Id == Guid.Empty) item.Id = Guid.NewGuid();
            item.UpdatedAt = DateTimeOffset.UtcNow;

            await conn.ExecuteAsync(upsertSql, new
            {
                item.Id,
                item.TaskName,
                item.ProviderUrl,
                item.Model,
                item.ApiKey,
                item.UpdatedAt
            }, tx);
        }

        await tx.CommitAsync(ct);
    }
}
