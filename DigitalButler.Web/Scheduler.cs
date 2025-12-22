using DigitalButler.Modules;
using DigitalButler.Modules.Repositories;
using Telegram.Bot;

namespace DigitalButler.Web;

public class SchedulerService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SchedulerService> _logger;

    public SchedulerService(IServiceProvider services, ILogger<SchedulerService> logger)
    {
        _services = services;
        _logger = logger;
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
        var summarizer = scope.ServiceProvider.GetRequiredService<ISummarizationService>();
        var nowUtc = DateTimeOffset.UtcNow;
        var tzService = scope.ServiceProvider.GetRequiredService<TimeZoneService>();
        var tz = await tzService.GetTimeZoneInfoAsync(ct);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var chatId = config["TELEGRAM_CHAT_ID"] ?? config["Telegram:ChatId"];
        ITelegramBotClient? bot = null;
        if (!string.IsNullOrWhiteSpace(chatId))
        {
            var token = config["TELEGRAM_BOT_TOKEN"];
            if (!string.IsNullOrWhiteSpace(token))
            {
                bot = new TelegramBotClient(token);
            }
        }
        var schedules = scope.ServiceProvider.GetRequiredService<ScheduleRepository>();

        // Run module updates if interval elapsed (simplified: run hourly if enabled)
        foreach (var schedule in await schedules.GetEnabledUpdateSchedulesAsync(ct))
        {
            // For now, run hourly based on CronOrInterval placeholder
            if (nowUtc.Minute == 0)
            {
                var updaters = scope.ServiceProvider.GetServices<IContextUpdater>().Where(u => u.Source == schedule.Source);
                foreach (var updater in updaters)
                {
                    await updater.UpdateAsync(ct);
                }
            }
        }

        // Daily summaries
        var daily = await schedules.GetEnabledDailySummarySchedulesAsync(ct);
        foreach (var sched in daily)
        {
            if (sched.Time.Hour == localNow.Hour && sched.Time.Minute == localNow.Minute)
            {
                await SendSummaryAsync(contextService, instructionService, summarizer, tz, "daily-summary", bot, chatId, ct);
            }
        }

        // Weekly summaries
        var weekly = await schedules.GetEnabledWeeklySummarySchedulesAsync(ct);
        foreach (var sched in weekly)
        {
            if (sched.DayOfWeek == localNow.DayOfWeek && sched.Time.Hour == localNow.Hour && sched.Time.Minute == localNow.Minute)
            {
                await SendSummaryAsync(contextService, instructionService, summarizer, tz, "weekly-summary", bot, chatId, ct);
            }
        }
    }

    private static async Task SendSummaryAsync(ContextService contextService, InstructionService instructionService, ISummarizationService summarizer, TimeZoneInfo tz, string taskName, ITelegramBotClient? bot, string? chatId, CancellationToken ct)
    {
        var items = taskName switch
        {
            "daily-summary" => await GetDailyItemsAsync(contextService, tz, ct),
            "weekly-summary" => await GetWeeklyItemsAsync(contextService, tz, ct),
            _ => await contextService.GetRelevantAsync(daysBack: 7, take: 200, ct: ct)
        };

        var sources = items.Select(x => x.Source).Distinct().ToArray();
        var instructionsBySource = await instructionService.GetBySourcesAsync(sources, ct);
        var summary = await summarizer.SummarizeAsync(items, instructionsBySource, taskName, ct);
        if (bot != null && !string.IsNullOrWhiteSpace(chatId))
        {
            await bot.SendTextMessageAsync(chatId, summary, cancellationToken: ct);
        }
    }

    private static Task<List<ContextItem>> GetDailyItemsAsync(ContextService contextService, TimeZoneInfo tz, CancellationToken ct)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);

        var localStart = new DateTime(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, tz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(localStart.AddDays(1), tz);

        return contextService.GetForWindowAsync(new DateTimeOffset(startUtc, TimeSpan.Zero), new DateTimeOffset(endUtc, TimeSpan.Zero), take: 200, ct: ct);
    }

    private static Task<List<ContextItem>> GetWeeklyItemsAsync(ContextService contextService, TimeZoneInfo tz, CancellationToken ct)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);

        var localTodayStart = new DateTime(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, DateTimeKind.Unspecified);

        // Week = Monday..Monday (exclusive), in the configured timezone.
        var diff = ((7 + (int)localNow.DayOfWeek - (int)DayOfWeek.Monday) % 7);
        var localWeekStart = localTodayStart.AddDays(-diff);
        var localWeekEnd = localWeekStart.AddDays(7);

        var weekStartUtc = TimeZoneInfo.ConvertTimeToUtc(localWeekStart, tz);
        var weekEndUtc = TimeZoneInfo.ConvertTimeToUtc(localWeekEnd, tz);

        return contextService.GetForWindowAsync(new DateTimeOffset(weekStartUtc, TimeSpan.Zero), new DateTimeOffset(weekEndUtc, TimeSpan.Zero), take: 300, ct: ct);
    }
}
