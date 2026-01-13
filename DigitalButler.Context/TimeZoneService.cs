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

        // Accept common fixed-offset shorthands like "UTC+4" by normalizing to a valid IANA ID.
        // (Google Calendar expects IANA IDs, and Linux TimeZoneInfo uses IANA.)
        trimmed = NormalizeTimeZoneId(trimmed);

        // Validate ID for current OS (IANA on Linux, Windows IDs on Windows).
        _ = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
        await _repo.UpsertAsync(TimeZoneKey, trimmed, ct);
    }

    public async Task<TimeZoneInfo> GetTimeZoneInfoAsync(CancellationToken ct = default)
    {
        var id = await GetTimeZoneIdAsync(ct);
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(NormalizeTimeZoneId(id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalid timezone '{TimeZoneId}', falling back to UTC", id);
            return TimeZoneInfo.Utc;
        }
    }

    private static string NormalizeTimeZoneId(string id)
    {
        var trimmed = (id ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "UTC";
        }

        if (TryMapFixedOffsetToEtcGmt(trimmed, out var mapped))
        {
            return mapped;
        }

        return trimmed;
    }

    private static bool TryMapFixedOffsetToEtcGmt(string value, out string mapped)
    {
        mapped = string.Empty;

        // Supported examples:
        // - "UTC+4", "UTC+04", "UTC+04:00", "GMT+4", "+04:00"
        // - "UTC-5", "-05:00"
        var s = value.Trim();
        if (s.StartsWith("UTC", StringComparison.OrdinalIgnoreCase))
        {
            s = s[3..].Trim();
        }
        else if (s.StartsWith("GMT", StringComparison.OrdinalIgnoreCase))
        {
            s = s[3..].Trim();
        }

        if (s.Length == 0)
        {
            return false;
        }

        // Only handle whole-hour offsets with Etc/GMT (Google/IANA compatible).
        // For non-whole-hour offsets users should set a real IANA zone like "Asia/Kabul".
        var sign = 1;
        if (s[0] == '+')
        {
            sign = 1;
            s = s[1..];
        }
        else if (s[0] == '-')
        {
            sign = -1;
            s = s[1..];
        }

        // Accept "4", "04", "04:00".
        var parts = s.Split(':', StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], out var hours))
        {
            return false;
        }

        var minutes = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], out minutes))
        {
            return false;
        }

        if (minutes != 0)
        {
            return false;
        }

        if (hours < 0 || hours > 14)
        {
            return false;
        }

        // IANA Etc/GMT has inverted sign: Etc/GMT-4 == UTC+4.
        var effective = sign * hours;
        mapped = effective switch
        {
            0 => "UTC",
            > 0 => $"Etc/GMT-{effective}",
            < 0 => $"Etc/GMT+{-effective}",
        };

        return true;
    }
}
