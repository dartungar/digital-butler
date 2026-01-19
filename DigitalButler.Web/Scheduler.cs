using DigitalButler.Context;
using DigitalButler.Common;
using DigitalButler.Data.Repositories;
using DigitalButler.Skills;
using Telegram.Bot;

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
        foreach (var schedule in await schedules.GetEnabledUpdateSchedulesAsync(ct))
        {
            var intervalMinutes = ParseIntervalMinutes(schedule.CronOrInterval);
            if (intervalMinutes > 0 && nowUtc.Minute % intervalMinutes == 0)
            {
                var updater = updaterRegistry.GetUpdater(schedule.Source);
                if (updater != null)
                {
                    try
                    {
                        await updater.UpdateAsync(ct);
                        _logger.LogInformation("Scheduled update for {Source} completed", schedule.Source);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Scheduled update for {Source} failed", schedule.Source);
                        if (_errorNotifier != null)
                        {
                            await _errorNotifier.NotifyErrorAsync($"Sync: {schedule.Source}", ex, ct);
                        }
                    }
                }
            }
        }

        // Daily summaries
        var daily = await schedules.GetEnabledDailySummarySchedulesAsync(ct);
        foreach (var sched in daily)
        {
            if (sched.Time.Hour == localNow.Hour && sched.Time.Minute == localNow.Minute)
            {
                try
                {
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
        foreach (var sched in weekly)
        {
            if (sched.DayOfWeek == localNow.DayOfWeek && sched.Time.Hour == localNow.Hour && sched.Time.Minute == localNow.Minute)
            {
                try
                {
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
        var (start, end) = taskName switch
        {
            "daily-summary" => TimeWindowHelper.GetDailyWindow(tz),
            "weekly-summary" => TimeWindowHelper.GetWeeklyWindow(tz),
            _ => TimeWindowHelper.GetDailyWindow(tz)
        };

        var items = await contextService.GetForWindowAsync(start, end, take: taskName == "weekly-summary" ? 300 : 200, ct: ct);

        var skill = taskName == "weekly-summary" ? ButlerSkill.WeeklySummary : ButlerSkill.DailySummary;
        var cfg = await skillInstructionService.GetFullBySkillsAsync(new[] { skill }, ct);
        cfg.TryGetValue(skill, out var summaryCfg);
        var allowedMask = SkillContextDefaults.ResolveSourcesMask(skill, summaryCfg?.ContextSourcesMask ?? -1);
        items = items.Where(x => ContextSourceMask.Contains(allowedMask, x.Source)).ToList();

        if (summaryCfg?.EnableAiContext == true)
        {
            var snippet = await aiContext.GenerateAsync(skill, items, taskName, ct);
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                items.Add(new ContextItem
                {
                    Source = ContextSource.Personal,
                    Title = "AI self-thought",
                    Body = snippet.Trim(),
                    IsTimeless = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        var sources = items.Select(x => x.Source).Distinct().ToArray();
        var instructionsBySource = await instructionService.GetBySourcesAsync(sources, ct);
        var custom = summaryCfg?.Content;
        var period = taskName.StartsWith("weekly", StringComparison.OrdinalIgnoreCase) ? "weekly" : "daily";
        var prompt = $"Skill: summary\nPeriod: {period}\nOutput a concise agenda with actionable highlights.\n" + (string.IsNullOrWhiteSpace(custom) ? string.Empty : "\n" + custom.Trim());
        var summary = await summarizer.SummarizeAsync(items, instructionsBySource, taskName, prompt, ct);
        if (_bot != null && !string.IsNullOrWhiteSpace(chatId))
        {
            await _bot.SendTextMessageAsync(chatId, summary, cancellationToken: ct);
            _logger.LogInformation("Sent {TaskName} to chat {ChatId}", taskName, chatId);
        }
        else
        {
            _logger.LogWarning("Cannot send {TaskName}: bot is {BotStatus}, chatId is {ChatIdStatus}",
                taskName,
                _bot == null ? "null" : "available",
                string.IsNullOrWhiteSpace(chatId) ? "missing" : "set");
        }

        // Store weekly summary for future comparisons
        if (taskName == "weekly-summary")
        {
            try
            {
                var obsidianAnalysis = serviceProvider.GetRequiredService<IObsidianAnalysisService>();
                var weeklyResult = await obsidianAnalysis.AnalyzeWeeklyAsync(tz, ct);
                if (weeklyResult != null)
                {
                    await obsidianAnalysis.StoreWeeklySummaryAsync(weeklyResult, summary, ct);
                    _logger.LogInformation("Stored weekly Obsidian summary for week starting {WeekStart}", weeklyResult.PeriodStart);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store weekly Obsidian summary");
            }
        }
    }
}

