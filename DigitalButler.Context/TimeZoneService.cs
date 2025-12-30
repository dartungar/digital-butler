using DigitalButler.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalButler.Context;

public sealed class TimeZoneService
{
    private const string TimeZoneKey = "timezone";

    private readonly ButlerOptions _options;
    private readonly AppSettingsRepository _repo;
    private readonly ILogger<TimeZoneService> _logger;

    public TimeZoneService(IOptions<ButlerOptions> options, AppSettingsRepository repo, ILogger<TimeZoneService> logger)
    {
        _options = options.Value;
        _repo = repo;
        _logger = logger;
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalid timezone '{TimeZoneId}', falling back to UTC", id);
            return TimeZoneInfo.Utc;
        }
    }
}
