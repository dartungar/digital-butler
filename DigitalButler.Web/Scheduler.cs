using System.Diagnostics;
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
        var contextService = scope.ServiceProvider.GetRequiredService<ContextService>();
        var instructionService = scope.ServiceProvider.GetRequiredService<InstructionService>();
        var skillInstructionService = scope.ServiceProvider.GetRequiredService<SkillInstructionService>();
        var summarizer = scope.ServiceProvider.GetRequiredService<ISummarizationService>();
        var aiContext = scope.ServiceProvider.GetRequiredService<IAiContextAugmenter>();
        var nowUtc = DateTimeOffset.UtcNow;
        var tzService = scope.ServiceProvider.GetRequiredService<TimeZoneService>();
        var tz = await tzService.GetTimeZoneInfoAsync(ct);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var chatId = _config["Telegram:ChatId"];
        var schedules = scope.ServiceProvider.GetRequiredService<ScheduleRepository>();
        var updaterRegistry = scope.ServiceProvider.GetRequiredService<IContextUpdaterRegistry>();

        // Run module updates based on interval (parse CronOrInterval as minutes interval)
        var logRepo = scope.ServiceProvider.GetRequiredService<ContextUpdateLogRepository>();
        foreach (var schedule in await schedules.GetEnabledUpdateSchedulesAsync(ct))
        {
            var intervalMinutes = ParseIntervalMinutes(schedule.CronOrInterval);
            if (intervalMinutes > 0 && nowUtc.Minute % intervalMinutes == 0)
            {
                var updater = updaterRegistry.GetUpdater(schedule.Source);
                if (updater != null)
                {
                    _logger.LogInformation("Starting scheduled update for {Source}", schedule.Source);
                    await RunUpdaterWithLoggingAsync(updater, logRepo, "scheduled", ct);
                }
            }
        }

        // Run vault indexing every 30 minutes (at :00 and :30)
        if (nowUtc.Minute % 30 == 0)
        {
            await RunVaultIndexingWithLoggingAsync(scope.ServiceProvider, logRepo, "scheduled", ct);
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
            if (sched.Time.Hour == localNow.Hour && sched.Time.Minute == localNow.Minute)
            {
                _logger.LogInformation("Daily summary schedule matched: {ScheduleTime} == {LocalNow:HH:mm}", sched.Time, localNow);
                try
                {
                    // Run all syncs before sending daily summary
                    _logger.LogInformation("Running pre-summary sync for daily summary");
                    await RunPreSummarySyncAsync(scope.ServiceProvider, updaterRegistry, logRepo, ct);
                    await SendSummaryAsync(scope.ServiceProvider, contextService, instructionService, skillInstructionService, summarizer, aiContext, tz, "daily-summary", chatId, ct);
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
            if (sched.DayOfWeek == localNow.DayOfWeek && sched.Time.Hour == localNow.Hour && sched.Time.Minute == localNow.Minute)
            {
                _logger.LogInformation("Weekly summary schedule matched: {DayOfWeek} {ScheduleTime} == {LocalDow} {LocalNow:HH:mm}",
                    sched.DayOfWeek, sched.Time, localNow.DayOfWeek, localNow);
                try
                {
                    // Run all syncs before sending weekly summary
                    _logger.LogInformation("Running pre-summary sync for weekly summary");
                    await RunPreSummarySyncAsync(scope.ServiceProvider, updaterRegistry, logRepo, ct);
                    await SendSummaryAsync(scope.ServiceProvider, contextService, instructionService, skillInstructionService, summarizer, aiContext, tz, "weekly-summary", chatId, ct);
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

    /// <summary>
    /// Parse CronOrInterval as interval in minutes. Supports:
    /// - Plain number: "60" = every 60 minutes
    /// - Cron-like: "0 */1 * * *" = extract interval from */N pattern, defaults to 60
    /// </summary>
    private static int ParseIntervalMinutes(string? cronOrInterval)
    {
        if (string.IsNullOrWhiteSpace(cronOrInterval))
            return 60; // Default: hourly

        var trimmed = cronOrInterval.Trim();

        // Try plain integer first
        if (int.TryParse(trimmed, out var plainMinutes) && plainMinutes > 0)
            return plainMinutes;

        // Try to extract from cron-like pattern "0 */N * * *" or similar
        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"\*/(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var cronInterval) && cronInterval > 0)
            return cronInterval * 60; // Assume it's hours, convert to minutes

        // Default to hourly
        return 60;
    }

    private async Task SendSummaryAsync(IServiceProvider serviceProvider, ContextService contextService, InstructionService instructionService, SkillInstructionService skillInstructionService, ISummarizationService summarizer, IAiContextAugmenter aiContext, TimeZoneInfo tz, string taskName, string? chatId, CancellationToken ct)
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
        }
        else
        {
            _logger.LogWarning("Cannot send {TaskName}: bot is {BotStatus}, chatId is {ChatIdStatus}",
                taskName,
                _bot == null ? "null" : "available",
                string.IsNullOrWhiteSpace(chatId) ? "missing" : "set");
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

