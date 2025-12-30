using DigitalButler.Context;
using DigitalButler.Data;
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

    public SchedulerService(
        IServiceProvider services,
        ILogger<SchedulerService> logger,
        IConfiguration config,
        ITelegramBotClient? bot = null)
    {
        _services = services;
        _logger = logger;
        _bot = bot;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduler tick failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var contextService = scope.ServiceProvider.GetRequiredService<ContextService>();
        var instructionService = scope.ServiceProvider.GetRequiredService<InstructionService>();
        var skillInstructionService = scope.ServiceProvider.GetRequiredService<SkillInstructionService>();
        var summarizer = scope.ServiceProvider.GetRequiredService<ISummarizationService>();
        var nowUtc = DateTimeOffset.UtcNow;
        var tzService = scope.ServiceProvider.GetRequiredService<TimeZoneService>();
        var tz = await tzService.GetTimeZoneInfoAsync(ct);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var chatId = _config["TELEGRAM_CHAT_ID"] ?? _config["Telegram:ChatId"];
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
                await SendSummaryAsync(contextService, instructionService, skillInstructionService, summarizer, tz, "daily-summary", chatId, ct);
            }
        }

        // Weekly summaries
        var weekly = await schedules.GetEnabledWeeklySummarySchedulesAsync(ct);
        foreach (var sched in weekly)
        {
            if (sched.DayOfWeek == localNow.DayOfWeek && sched.Time.Hour == localNow.Hour && sched.Time.Minute == localNow.Minute)
            {
                await SendSummaryAsync(contextService, instructionService, skillInstructionService, summarizer, tz, "weekly-summary", chatId, ct);
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

    private async Task SendSummaryAsync(ContextService contextService, InstructionService instructionService, SkillInstructionService skillInstructionService, ISummarizationService summarizer, TimeZoneInfo tz, string taskName, string? chatId, CancellationToken ct)
    {
        var (start, end) = taskName switch
        {
            "daily-summary" => TimeWindowHelper.GetDailyWindow(tz),
            "weekly-summary" => TimeWindowHelper.GetWeeklyWindow(tz),
            _ => TimeWindowHelper.GetDailyWindow(tz)
        };

        var items = await contextService.GetForWindowAsync(start, end, take: taskName == "weekly-summary" ? 300 : 200, ct: ct);

        var sources = items.Select(x => x.Source).Distinct().ToArray();
        var instructionsBySource = await instructionService.GetBySourcesAsync(sources, ct);
        var skillInstructions = await skillInstructionService.GetBySkillsAsync(new[] { ButlerSkill.Summary }, ct);
        skillInstructions.TryGetValue(ButlerSkill.Summary, out var custom);
        var period = taskName.StartsWith("weekly", StringComparison.OrdinalIgnoreCase) ? "weekly" : "daily";
        var prompt = $"Skill: summary\nPeriod: {period}\nOutput a concise agenda with actionable highlights.\n" + (string.IsNullOrWhiteSpace(custom) ? string.Empty : "\n" + custom.Trim());
        var summary = await summarizer.SummarizeAsync(items, instructionsBySource, taskName, prompt, ct);
        if (_bot != null && !string.IsNullOrWhiteSpace(chatId))
        {
            await _bot.SendTextMessageAsync(chatId, summary, cancellationToken: ct);
        }
    }
}

