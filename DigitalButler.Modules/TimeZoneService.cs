using DigitalButler.Modules.Repositories;
using Microsoft.Extensions.Options;

namespace DigitalButler.Modules;

public sealed class TimeZoneService
{
    private const string TimeZoneKey = "timezone";

    private readonly ButlerOptions _options;
    private readonly AppSettingsRepository _repo;

    public TimeZoneService(IOptions<ButlerOptions> options, AppSettingsRepository repo)
    {
        _options = options.Value;
        _repo = repo;
    }

    public string? GetConfiguredTimeZoneIdOverride()
    {
        // Env/config override takes precedence over DB.
        return _options.TimeZone;
    }

    public async Task<string> GetTimeZoneIdAsync(CancellationToken ct = default)
    {
        var overrideId = GetConfiguredTimeZoneIdOverride();
        if (!string.IsNullOrWhiteSpace(overrideId))
        {
            return overrideId.Trim();
        }

        var dbValue = await _repo.GetAsync(TimeZoneKey, ct);
        if (!string.IsNullOrWhiteSpace(dbValue))
        {
            return dbValue.Trim();
        }

        return "UTC";
    }

    public async Task SetTimeZoneIdAsync(string timeZoneId, CancellationToken ct = default)
    {
        var trimmed = (timeZoneId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            trimmed = "UTC";
        }

        // Validate ID for current OS (IANA on Linux, Windows IDs on Windows).
        _ = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
        await _repo.UpsertAsync(TimeZoneKey, trimmed, ct);
    }

    public async Task<TimeZoneInfo> GetTimeZoneInfoAsync(CancellationToken ct = default)
    {
        var id = await GetTimeZoneIdAsync(ct);
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }
}
