using DigitalButler.Context.Repositories;

namespace DigitalButler.Context;

public class ContextService
{
    private readonly ContextRepository _repo;

    public ContextService(ContextRepository repo)
    {
        _repo = repo;
    }

    public async Task<ContextItem> AddPersonalAsync(string body, string? title = null, DateTimeOffset? relevantDate = null, bool isTimeless = true, string? category = null, CancellationToken ct = default)
    {
        var item = new ContextItem
        {
            Id = Guid.NewGuid(),
            Source = ContextSource.Personal,
            Title = string.IsNullOrWhiteSpace(title) ? "Note" : title,
            Body = body,
            RelevantDate = relevantDate,
            IsTimeless = isTimeless,
            Category = category,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return await _repo.InsertAsync(item, ct);
    }

    public Task<List<ContextItem>> GetRecentAsync(int take = 200, CancellationToken ct = default) =>
        _repo.GetRecentAsync(take, ct);

    public Task<List<ContextItem>> GetRelevantAsync(int daysBack = 7, int take = 200, CancellationToken ct = default)
        => _repo.GetRelevantAsync(daysBack, take, ct);

    public Task<List<ContextItem>> GetForWindowAsync(DateTimeOffset windowStartInclusive, DateTimeOffset windowEndExclusive, int take = 200, CancellationToken ct = default)
        => _repo.GetForWindowAsync(windowStartInclusive, windowEndExclusive, take, ct);

    public async Task<bool> UpdateAsync(Guid id, string title, string body, string? category, bool isTimeless, DateTimeOffset? relevantDate, CancellationToken ct = default)
    {
        var item = new ContextItem
        {
            Id = id,
            Title = title,
            Body = body,
            Category = category,
            IsTimeless = isTimeless,
            RelevantDate = relevantDate,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        return await _repo.UpdateAsync(item, ct) > 0;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        return await _repo.DeleteAsync(id, ct) > 0;
    }

    public Task<int> DeleteManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
        => _repo.DeleteManyAsync(ids, ct);

    public Task<int> DeleteByCategoryAsync(string? category, CancellationToken ct = default)
        => _repo.DeleteByCategoryAsync(category, ct);

    public Task<int> DeleteBySourceAsync(ContextSource source, CancellationToken ct = default)
        => _repo.DeleteBySourceAsync(source, ct);

    public Task<int> CountByCategoryAsync(string? category, CancellationToken ct = default)
        => _repo.CountByCategoryAsync(category, ct);

    public Task<int> CountBySourceAsync(ContextSource source, CancellationToken ct = default)
        => _repo.CountBySourceAsync(source, ct);

    public Task<List<string?>> GetDistinctCategoriesAsync(CancellationToken ct = default)
        => _repo.GetDistinctCategoriesAsync(ct);
}

public class InstructionService
{
    private readonly InstructionRepository _repo;

    public InstructionService(InstructionRepository repo)
    {
        _repo = repo;
    }

    public async Task<string> GetMergedAsync(CancellationToken ct = default)
    {
        return await _repo.GetMergedAsync(ct);
    }

    public Task<Dictionary<ContextSource, string>> GetBySourcesAsync(IEnumerable<ContextSource> sources, CancellationToken ct = default)
        => _repo.GetBySourcesAsync(sources, ct);
}

public class AiTaskSettingsService
{
    private readonly AiTaskSettingRepository _repo;

    public AiTaskSettingsService(AiTaskSettingRepository repo)
    {
        _repo = repo;
    }

    public Task<AiTaskSetting?> GetAsync(string taskName, CancellationToken ct = default) =>
        _repo.GetByTaskNameAsync(taskName, ct);

    public async Task<AiTaskSetting> UpsertAsync(string taskName, string? baseUrl, string? model, string? apiKey, CancellationToken ct = default)
    {
        var existing = await _repo.GetByTaskNameAsync(taskName, ct);
        var row = existing ?? new AiTaskSetting { Id = Guid.NewGuid(), TaskName = taskName };
        row.ProviderUrl = baseUrl;
        row.Model = model;
        row.ApiKey = apiKey;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await _repo.UpsertAsync(row, ct);
        return row;
    }
}

