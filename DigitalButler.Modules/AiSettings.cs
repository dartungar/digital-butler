using Microsoft.Extensions.Options;
using DigitalButler.Modules.Repositories;

namespace DigitalButler.Modules;

public class AiDefaults
{
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
}

public class AiSettings
{
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
}

public class AiSettingsResolver
{
    private readonly AiTaskSettingRepository _repo;
    private readonly AiDefaults _defaults;

    public AiSettingsResolver(AiTaskSettingRepository repo, IOptions<AiDefaults> defaults)
    {
        _repo = repo;
        _defaults = defaults.Value;
    }

    public async Task<AiSettings> ResolveAsync(string taskName, CancellationToken ct = default)
    {
        var setting = await _repo.GetByTaskNameAsync(taskName, ct);
        return new AiSettings
        {
            BaseUrl = setting?.ProviderUrl ?? _defaults.BaseUrl,
            Model = setting?.Model ?? _defaults.Model,
            ApiKey = setting?.ApiKey ?? _defaults.ApiKey
        };
    }
}
