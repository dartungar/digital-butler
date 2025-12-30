using DigitalButler.Data;
using DigitalButler.Data.Repositories;

namespace DigitalButler.Skills;

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

public class SkillInstructionService
{
    private readonly SkillInstructionRepository _repo;

    public SkillInstructionService(SkillInstructionRepository repo)
    {
        _repo = repo;
    }

    public Task<Dictionary<ButlerSkill, string>> GetBySkillsAsync(IEnumerable<ButlerSkill> skills, CancellationToken ct = default)
        => _repo.GetBySkillsAsync(skills, ct);
}
