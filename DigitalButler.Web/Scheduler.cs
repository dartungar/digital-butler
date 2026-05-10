using System.Diagnostics;
using System.Globalization;
using DigitalButler.Context;
using DigitalButler.Common;
using DigitalButler.Data.Repositories;
using DigitalButler.Skills;
using DigitalButler.Skills.VaultSearch;
using DigitalButler.Telegram.Skills;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace DigitalButler.Web;

public class SchedulerService : BackgroundService
{
    private const string VaultIndexLastRunKey = "scheduler.vaultIndex.lastRun";
    private static readonly TimeSpan DailySummaryCatchUpWindow = TimeSpan.FromHours(12);
    private static readonly TimeSpan WeeklySummaryCatchUpWindow = TimeSpan.FromDays(1);

    private readonly IServiceProvider _services;
    private readonly ILogger<SchedulerService> _logger;
    private readonly ITelegramBotClient? _bot;
    private readonly IConfiguration _config;
    private readonly ITelegramErrorNotifier? _errorNotifier;

    public SchedulerService(
        IServiceProvider services,
        ILogger<SchedulerService> logger,
        IConfiguration config,
        ITelegramBotClient? bot = null,
        ITelegramErrorNotifier? errorNotifier = null)
    {
        _services = services;
        _logger = logger;
        _bot = bot;
        _config = config;
        _errorNotifier = errorNotifier;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var chatId = _config["Telegram:ChatId"];
            _logger.LogInformation("Scheduler starting. Bot: {BotStatus}, ChatId: {ChatIdStatus}",
                _bot == null ? "not configured" : "configured",
                string.IsNullOrWhiteSpace(chatId) ? "not set" : $"set ({chatId})");

            // Log configured schedules at startup
            using (var scope = _services.CreateScope())
            {
                var schedules = scope.ServiceProvider.GetRequiredService<ScheduleRepository>();
                var tzService = scope.ServiceProvider.GetRequiredService<TimeZoneService>();
                var tz = await tzService.GetTimeZoneInfoAsync(stoppingToken);
                var daily = await schedules.GetEnabledDailySummarySchedulesAsync(stoppingToken);
                var weekly = await schedules.GetEnabledWeeklySummarySchedulesAsync(stoppingToken);
                _logger.LogInformation("Timezone: {Timezone}, Daily schedules: [{DailyTimes}], Weekly schedules: [{WeeklyTimes}]",
                    tz.Id,
                    daily.Count == 0 ? "none" : string.Join(", ", daily.Select(s => s.Time.ToString("HH:mm"))),
                    weekly.Count == 0 ? "none" : string.Join(", ", weekly.Select(s => $"{s.DayOfWeek} {s.Time:HH:mm}")));
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduler tick failed");
                    if (_errorNotifier != null)
                    {
                        await _errorNotifier.NotifyErrorAsync("Scheduler tick", ex, stoppingToken);
                    }
                }
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduler failed to start");
            throw;
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var nowUtc = DateTimeOffset.UtcNow;
        var tzService = scope.ServiceProvider.GetRequiredService<TimeZoneService>();
        var tz = await tzService.GetTimeZoneInfoAsync(ct);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var chatId = _config["Telegram:ChatId"];
        var schedules = scope.ServiceProvider.GetRequiredService<ScheduleRepository>();
        var updaterRegistry = scope.ServiceProvider.GetRequiredService<IContextUpdaterRegistry>();
        var appSettings = scope.ServiceProvider.GetRequiredService<AppSettingsRepository>();

        // Run module updates based on interval (parse CronOrInterval as minutes interval)
        var logRepo = scope.ServiceProvider.GetRequiredService<ContextUpdateLogRepository>();
        foreach (var schedule in await schedules.GetEnabledUpdateSchedulesAsync(ct))
        {
            if (await IsUpdateScheduleDueAsync(schedule, nowUtc, appSettings, ct))
            {
                var updater = updaterRegistry.GetUpdater(schedule.Source);
                if (updater != null)
                {
                    _logger.LogInformation("Starting scheduled update for {Source}", schedule.Source);
                    await RunUpdaterWithLoggingAsync(updater, logRepo, "scheduled", ct);
                    await MarkRunAsync(UpdateScheduleLastRunKey(schedule), nowUtc, appSettings, ct);
                }
            }
        }

        if (await IsIntervalDueAsync(VaultIndexLastRunKey, TimeSpan.FromMinutes(30), nowUtc, appSettings, ct))
        {
            await RunVaultIndexingWithLoggingAsync(scope.ServiceProvider, logRepo, "scheduled", ct);
            await MarkRunAsync(VaultIndexLastRunKey, nowUtc, appSettings, ct);
        }

        // Daily summaries
        var daily = await schedules.GetEnabledDailySummarySchedulesAsync(ct);
        if (localNow.Minute == 0) // Log once per hour to avoid spam
        {
            _logger.LogInformation("Scheduler tick: localNow={LocalNow:HH:mm}, daily schedules count={Count}, times=[{Times}]",
                localNow,
                daily.Count,
                string.Join(", ", daily.Select(s => s.Time.ToString("HH:mm"))));
        }
        foreach (var sched in daily)
        {
            var due = await GetDueSummaryOccurrenceAsync(sched, localNow, nowUtc, tz, appSettings, ct);
            if (due is not null)
            {
                _logger.LogInformation("Daily summary schedule due: {ScheduledLocal}", due.ScheduledLocal);
                try
                {
                    // Run all syncs before sending daily summary
                    _logger.LogInformation("Running pre-summary sync for daily summary");
                    await RunPreSummarySyncAsync(scope.ServiceProvider, updaterRegistry, logRepo, ct);
                    if (await SendSummaryAsync(scope.ServiceProvider, "daily-summary", chatId, ct))
                    {
                        await appSettings.UpsertAsync(due.SentKey, nowUtc.ToString("O"), ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send daily summary");
                    if (_errorNotifier != null)
                    {
                        await _errorNotifier.NotifyErrorAsync("Daily summary", ex, ct);
                    }
                }
            }
        }

        // Weekly summaries
        var weekly = await schedules.GetEnabledWeeklySummarySchedulesAsync(ct);
        if (localNow.Minute == 0) // Log once per hour to avoid spam
        {
            _logger.LogInformation("Weekly schedules count={Count}, times=[{Times}]",
                weekly.Count,
                string.Join(", ", weekly.Select(s => $"{s.DayOfWeek} {s.Time:HH:mm}")));
        }
        foreach (var sched in weekly)
        {
            var due = await GetDueSummaryOccurrenceAsync(sched, localNow, nowUtc, tz, appSettings, ct);
            if (due is not null)
            {
                _logger.LogInformation("Weekly summary schedule due: {ScheduledLocal}", due.ScheduledLocal);
                try
                {
                    // Run all syncs before sending weekly summary
                    _logger.LogInformation("Running pre-summary sync for weekly summary");
                    await RunPreSummarySyncAsync(scope.ServiceProvider, updaterRegistry, logRepo, ct);
                    if (await SendSummaryAsync(scope.ServiceProvider, "weekly-summary", chatId, ct))
                    {
                        await appSettings.UpsertAsync(due.SentKey, nowUtc.ToString("O"), ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send weekly summary");
                    if (_errorNotifier != null)
                    {
                        await _errorNotifier.NotifyErrorAsync("Weekly summary", ex, ct);
                    }
                }
            }
        }
    }

    private async Task<bool> IsUpdateScheduleDueAsync(
        ScheduleConfig schedule,
        DateTimeOffset nowUtc,
        AppSettingsRepository appSettings,
        CancellationToken ct)
    {
        var key = UpdateScheduleLastRunKey(schedule);
        var raw = schedule.CronOrInterval;

        if (TryParseInterval(raw, out var interval))
        {
            return await IsIntervalDueAsync(key, interval, nowUtc, appSettings, ct);
        }

        if (CronExpression.TryParse(raw, out var cron))
        {
            var lastRun = await GetLastRunAsync(key, appSettings, ct);
            if (lastRun is null)
            {
                return cron.IsMatch(TruncateToMinute(nowUtc).UtcDateTime);
            }

            return cron.HasOccurrenceBetween(lastRun.Value, nowUtc);
        }

        _logger.LogWarning("Unsupported update schedule '{Schedule}' for {Source}; falling back to hourly elapsed checks", raw, schedule.Source);
        return await IsIntervalDueAsync(key, TimeSpan.FromHours(1), nowUtc, appSettings, ct);
    }

    private static async Task<bool> IsIntervalDueAsync(
        string key,
        TimeSpan interval,
        DateTimeOffset nowUtc,
        AppSettingsRepository appSettings,
        CancellationToken ct)
    {
        var lastRun = await GetLastRunAsync(key, appSettings, ct);
        return lastRun is null || nowUtc - lastRun.Value >= interval;
    }

    private static async Task<DateTimeOffset?> GetLastRunAsync(string key, AppSettingsRepository appSettings, CancellationToken ct)
    {
        var raw = await appSettings.GetAsync(key, ct);
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
        {
            return value.ToUniversalTime();
        }

        return null;
    }

    private static Task MarkRunAsync(string key, DateTimeOffset nowUtc, AppSettingsRepository appSettings, CancellationToken ct)
    {
        return appSettings.UpsertAsync(key, nowUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), ct);
    }

    private static bool TryParseInterval(string? value, out TimeSpan interval)
    {
        interval = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            interval = TimeSpan.FromHours(1);
            return true;
        }

        var trimmed = value.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
        {
            interval = TimeSpan.FromMinutes(minutes);
            return true;
        }

        return false;
    }

    private async Task<SummaryOccurrence?> GetDueSummaryOccurrenceAsync(
        SummarySchedule schedule,
        DateTimeOffset localNow,
        DateTimeOffset nowUtc,
        TimeZoneInfo tz,
        AppSettingsRepository appSettings,
        CancellationToken ct)
    {
        var scheduledLocal = schedule.IsWeekly
            ? GetMostRecentWeeklyScheduledLocal(schedule, localNow)
            : GetMostRecentDailyScheduledLocal(schedule, localNow);

        if (scheduledLocal is null)
        {
            return null;
        }

        var scheduledUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(scheduledLocal.Value, tz), TimeSpan.Zero);
        var catchUpWindow = schedule.IsWeekly ? WeeklySummaryCatchUpWindow : DailySummaryCatchUpWindow;
        if (scheduledUtc > nowUtc || nowUtc - scheduledUtc > catchUpWindow)
        {
            return null;
        }

        var sentKey = SummarySentKey(schedule, scheduledLocal.Value);
        var existing = await appSettings.GetAsync(sentKey, ct);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return null;
        }

        return new SummaryOccurrence(scheduledLocal.Value, sentKey);
    }

    private static DateTime? GetMostRecentDailyScheduledLocal(SummarySchedule schedule, DateTimeOffset localNow)
    {
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var candidate = today.ToDateTime(schedule.Time);
        if (candidate > localNow.DateTime)
        {
            candidate = candidate.AddDays(-1);
        }

        return candidate;
    }

    private static DateTime? GetMostRecentWeeklyScheduledLocal(SummarySchedule schedule, DateTimeOffset localNow)
    {
        if (schedule.DayOfWeek is null)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(localNow.DateTime);
        var daysBack = ((7 + (int)localNow.DayOfWeek - (int)schedule.DayOfWeek.Value) % 7);
        var candidate = today.AddDays(-daysBack).ToDateTime(schedule.Time);
        if (candidate > localNow.DateTime)
        {
            candidate = candidate.AddDays(-7);
        }

        return candidate;
    }

    private static string UpdateScheduleLastRunKey(ScheduleConfig schedule)
        => $"scheduler.update.lastRun.{schedule.Source}.{schedule.Id:N}";

    private static string SummarySentKey(SummarySchedule schedule, DateTime scheduledLocal)
    {
        var kind = schedule.IsWeekly ? "weekly" : "daily";
        return $"scheduler.summary.sent.{kind}.{schedule.Id:N}.{scheduledLocal.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}";
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Offset);
    }

    private sealed record SummaryOccurrence(DateTime ScheduledLocal, string SentKey);

    private sealed class CronExpression
    {
        private readonly HashSet<int> _minutes;
        private readonly HashSet<int> _hours;
        private readonly HashSet<int> _days;
        private readonly HashSet<int> _months;
        private readonly HashSet<int> _daysOfWeek;

        private CronExpression(
            HashSet<int> minutes,
            HashSet<int> hours,
            HashSet<int> days,
            HashSet<int> months,
            HashSet<int> daysOfWeek)
        {
            _minutes = minutes;
            _hours = hours;
            _days = days;
            _months = months;
            _daysOfWeek = daysOfWeek;
        }

        public static bool TryParse(string? value, out CronExpression expression)
        {
            expression = null!;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 5)
            {
                return false;
            }

            if (!TryParseField(parts[0], 0, 59, allowSundaySeven: false, out var minutes) ||
                !TryParseField(parts[1], 0, 23, allowSundaySeven: false, out var hours) ||
                !TryParseField(parts[2], 1, 31, allowSundaySeven: false, out var days) ||
                !TryParseField(parts[3], 1, 12, allowSundaySeven: false, out var months) ||
                !TryParseField(parts[4], 0, 6, allowSundaySeven: true, out var daysOfWeek))
            {
                return false;
            }

            expression = new CronExpression(minutes, hours, days, months, daysOfWeek);
            return true;
        }

        public bool HasOccurrenceBetween(DateTimeOffset lastExclusiveUtc, DateTimeOffset nowInclusiveUtc)
        {
            var cursor = TruncateToMinute(lastExclusiveUtc.ToUniversalTime()).AddMinutes(1);
            var end = TruncateToMinute(nowInclusiveUtc.ToUniversalTime());

            var earliest = end.AddDays(-7);
            if (cursor < earliest)
            {
                cursor = earliest;
            }

            while (cursor <= end)
            {
                if (IsMatch(cursor.UtcDateTime))
                {
                    return true;
                }

                cursor = cursor.AddMinutes(1);
            }

            return false;
        }

        public bool IsMatch(DateTime utc)
        {
            return _minutes.Contains(utc.Minute) &&
                   _hours.Contains(utc.Hour) &&
                   _days.Contains(utc.Day) &&
                   _months.Contains(utc.Month) &&
                   _daysOfWeek.Contains((int)utc.DayOfWeek);
        }

        private static bool TryParseField(string field, int min, int max, bool allowSundaySeven, out HashSet<int> values)
        {
            values = new HashSet<int>();

            foreach (var rawPart in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var part = rawPart;
                var step = 1;
                var slash = part.IndexOf('/');
                if (slash >= 0)
                {
                    if (!int.TryParse(part[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out step) || step <= 0)
                    {
                        return false;
                    }

                    part = part[..slash];
                }

                int start;
                int end;
                if (part == "*")
                {
                    start = min;
                    end = max;
                }
                else
                {
                    var dash = part.IndexOf('-');
                    if (dash >= 0)
                    {
                        if (!TryParseCronValue(part[..dash], min, max, allowSundaySeven, out start) ||
                            !TryParseCronValue(part[(dash + 1)..], min, max, allowSundaySeven, out end) ||
                            start > end)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        if (!TryParseCronValue(part, min, max, allowSundaySeven, out start))
                        {
                            return false;
                        }

                        end = start;
                    }
                }

                for (var value = start; value <= end; value += step)
                {
                    values.Add(value);
                }
            }

            return values.Count > 0;
        }

        private static bool TryParseCronValue(string value, int min, int max, bool allowSundaySeven, out int parsed)
        {
            parsed = default;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return false;
            }

            if (allowSundaySeven && parsed == 7)
            {
                parsed = 0;
            }

            return parsed >= min && parsed <= max;
        }
    }

    private async Task<bool> SendSummaryAsync(IServiceProvider serviceProvider, string taskName, string? chatId, CancellationToken ct)
    {
        // Use SummarySkillExecutor to ensure consistent behavior with manual /daily and /weekly commands
        var summaryExecutor = serviceProvider.GetRequiredService<ISummarySkillExecutor>();
        var weekly = taskName == "weekly-summary";
        var summary = await summaryExecutor.ExecuteAsync(weekly, taskName, ct);

        if (_bot != null && !string.IsNullOrWhiteSpace(chatId))
        {
            try
            {
                await _bot.SendTextMessageAsync(chatId, summary, parseMode: ParseMode.Markdown, cancellationToken: ct);
            }
            catch (global::Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase))
            {
                // Markdown parsing failed, retry without parsing
                await _bot.SendTextMessageAsync(chatId, summary, cancellationToken: ct);
            }
            _logger.LogInformation("Sent {TaskName} to chat {ChatId}", taskName, chatId);
            return true;
        }
        else
        {
            _logger.LogWarning("Cannot send {TaskName}: bot is {BotStatus}, chatId is {ChatIdStatus}",
                taskName,
                _bot == null ? "null" : "available",
                string.IsNullOrWhiteSpace(chatId) ? "missing" : "set");
            return false;
        }
    }

    /// <summary>
    /// Runs all context updaters and vault indexing before sending a summary.
    /// This ensures fresh data is available for summary generation.
    /// </summary>
    private async Task RunPreSummarySyncAsync(IServiceProvider serviceProvider, IContextUpdaterRegistry updaterRegistry, ContextUpdateLogRepository logRepo, CancellationToken ct)
    {
        _logger.LogInformation("Pre-summary sync started");
        var sw = Stopwatch.StartNew();

        foreach (var updater in updaterRegistry.GetAll())
        {
            await RunUpdaterWithLoggingAsync(updater, logRepo, "pre-summary", ct);
        }

        // Also run vault indexing
        await RunVaultIndexingWithLoggingAsync(serviceProvider, logRepo, "pre-summary", ct);

        sw.Stop();
        _logger.LogInformation("Pre-summary sync completed in {DurationMs}ms", sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Runs a single context updater with full logging to ContextUpdateLog.
    /// </summary>
    private async Task RunUpdaterWithLoggingAsync(IContextUpdater updater, ContextUpdateLogRepository logRepo, string trigger, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var log = new ContextUpdateLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            Source = updater.Source.ToString(),
            Message = trigger
        };

        try
        {
            _logger.LogInformation("Starting {Trigger} update for {Source}", trigger, updater.Source);
            await updater.UpdateAsync(ct);
            sw.Stop();

            log.Status = "Success";
            log.DurationMs = (int)sw.ElapsedMilliseconds;
            _logger.LogInformation("{Trigger} update for {Source} completed in {DurationMs}ms",
                trigger, updater.Source, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            log.Status = "Failed";
            log.DurationMs = (int)sw.ElapsedMilliseconds;
            log.Details = $"{ex.GetType().Name}: {ex.Message}";

            _logger.LogWarning(ex, "{Trigger} update for {Source} failed after {DurationMs}ms",
                trigger, updater.Source, sw.ElapsedMilliseconds);

            if (_errorNotifier != null)
            {
                await _errorNotifier.NotifyErrorAsync($"Sync: {updater.Source}", ex, ct);
            }
        }

        try
        {
            await logRepo.AddAsync(log, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log context update for {Source}", updater.Source);
        }
    }

    /// <summary>
    /// Runs vault indexing with full logging to ContextUpdateLog.
    /// </summary>
    private async Task RunVaultIndexingWithLoggingAsync(IServiceProvider serviceProvider, ContextUpdateLogRepository logRepo, string trigger, CancellationToken ct)
    {
        var vaultIndexer = serviceProvider.GetService<IVaultIndexer>();
        if (vaultIndexer == null)
            return;

        var sw = Stopwatch.StartNew();
        var log = new ContextUpdateLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            Source = "VaultIndexing",
            Message = trigger
        };

        try
        {
            _logger.LogInformation("Starting {Trigger} vault indexing", trigger);
            var result = await vaultIndexer.IndexVaultAsync(ct);
            sw.Stop();

            log.Status = "Success";
            log.ItemsAdded = result.NotesAdded;
            log.ItemsUpdated = result.NotesUpdated;
            log.ItemsScanned = result.ChunksCreated;
            log.DurationMs = (int)sw.ElapsedMilliseconds;
            log.Details = $"Removed: {result.NotesRemoved}";

            if (result.NotesAdded > 0 || result.NotesUpdated > 0 || result.NotesRemoved > 0)
            {
                _logger.LogInformation(
                    "{Trigger} vault indexing completed: {Added} added, {Updated} updated, {Removed} removed, {Chunks} chunks in {DurationMs}ms",
                    trigger, result.NotesAdded, result.NotesUpdated, result.NotesRemoved, result.ChunksCreated, sw.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogInformation("{Trigger} vault indexing completed (no changes) in {DurationMs}ms", trigger, sw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            log.Status = "Failed";
            log.DurationMs = (int)sw.ElapsedMilliseconds;
            log.Details = $"{ex.GetType().Name}: {ex.Message}";

            _logger.LogWarning(ex, "{Trigger} vault indexing failed after {DurationMs}ms", trigger, sw.ElapsedMilliseconds);

            if (_errorNotifier != null)
            {
                await _errorNotifier.NotifyErrorAsync("Vault indexing", ex, ct);
            }
        }

        try
        {
            await logRepo.AddAsync(log, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log vault indexing");
        }
    }
}
