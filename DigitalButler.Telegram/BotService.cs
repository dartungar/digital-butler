using DigitalButler.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Polling = Telegram.Bot.Polling;
using Telegram.Bot.Types;

namespace DigitalButler.Telegram;

public class BotService : Microsoft.Extensions.Hosting.IHostedService, IDisposable
{
    private readonly ILogger<BotService> _logger;
    private readonly IServiceProvider _services;
    private readonly string _token;
    private TelegramBotClient? _bot;
    private CancellationTokenSource? _cts;
    private Task? _runLoop;

    public BotService(ILogger<BotService> logger, IServiceProvider services, IConfiguration config)
    {
        _logger = logger;
        _services = services;
        _token = config["TELEGRAM_BOT_TOKEN"] ?? throw new InvalidOperationException("TELEGRAM_BOT_TOKEN not configured");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Never fail host startup due to Telegram/network issues.
        // Instead, keep retrying in the background until Telegram becomes reachable.
        _runLoop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
        }
        if (_runLoop is not null)
        {
            try
            {
                await _runLoop;
            }
            catch (OperationCanceledException)
            {
                // expected
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Bot background loop stopped with error");
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        var receiverOptions = new Polling.ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _bot = new TelegramBotClient(_token);

                // Avoid hanging forever on startup (TLS/network issues). Keep it short and retry.
                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pingCts.CancelAfter(TimeSpan.FromSeconds(10));

                var me = await _bot.GetMeAsync(cancellationToken: pingCts.Token);
                _logger.LogInformation("Bot connected as {Username}", me.Username);

                _bot.StartReceiving(
                    updateHandler: HandleUpdateAsync,
                    pollingErrorHandler: HandleErrorAsync,
                    receiverOptions: receiverOptions,
                    cancellationToken: ct);

                // Once receiving is started, just wait until we are cancelled.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram bot failed to start; retrying in {Delay}", delay);
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }

                // Exponential backoff up to 60s.
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Type != UpdateType.Message || update.Message?.Text is null)
            return;

        var chatId = update.Message.Chat.Id;
        var text = update.Message.Text.Trim();

        using var scope = _services.CreateScope();
        var contextService = scope.ServiceProvider.GetRequiredService<ContextService>();
        var instructionService = scope.ServiceProvider.GetRequiredService<InstructionService>();
        var summarizer = scope.ServiceProvider.GetRequiredService<ISummarizationService>();
        var tzService = scope.ServiceProvider.GetRequiredService<TimeZoneService>();

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await SendWithKeyboardAsync(bot, chatId,
                "Commands:\n" +
                "/daily - today + timeless\n" +
                "/weekly - this week + timeless\n" +
                "/add <text> - add personal context\n" +
                "/sync - run all ingests now",
                cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/add", StringComparison.OrdinalIgnoreCase))
        {
            var content = text[4..].Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                await SendWithKeyboardAsync(bot, chatId, "Usage: /add your note", cancellationToken: ct);
                return;
            }

            await contextService.AddPersonalAsync(content, ct: ct);
            await SendWithKeyboardAsync(bot, chatId, "Saved", cancellationToken: ct);
            return;
        }

        // Backwards compat: /summary behaves like /daily.
        if (text.StartsWith("/summary", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/daily", StringComparison.OrdinalIgnoreCase))
        {
            await SendWithKeyboardAsync(bot, chatId, "Generating daily summary...", cancellationToken: ct);
            try
            {
                var tz = await tzService.GetTimeZoneInfoAsync(ct);
                var items = await GetDailyItemsAsync(contextService, tz, ct);
                var sources = items.Select(x => x.Source).Distinct().ToArray();
                var instructionsBySource = await instructionService.GetBySourcesAsync(sources, ct);
                var summary = await summarizer.SummarizeAsync(items, instructionsBySource, "on-demand-daily", ct);
                if (string.IsNullOrWhiteSpace(summary)) summary = "No summary available.";
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(summary), cancellationToken: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host is shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate /daily");
                var msg = BuildUserFacingError(ex);
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(msg), cancellationToken: ct);
            }
            return;
        }

        if (text.StartsWith("/weekly", StringComparison.OrdinalIgnoreCase))
        {
            await SendWithKeyboardAsync(bot, chatId, "Generating weekly summary...", cancellationToken: ct);
            try
            {
                var tz = await tzService.GetTimeZoneInfoAsync(ct);
                var items = await GetWeeklyItemsAsync(contextService, tz, ct);
                var sources = items.Select(x => x.Source).Distinct().ToArray();
                var instructionsBySource = await instructionService.GetBySourcesAsync(sources, ct);
                var summary = await summarizer.SummarizeAsync(items, instructionsBySource, "on-demand-weekly", ct);
                if (string.IsNullOrWhiteSpace(summary)) summary = "No summary available.";
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(summary), cancellationToken: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host is shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate /weekly");
                var msg = BuildUserFacingError(ex);
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(msg), cancellationToken: ct);
            }

            return;
        }

        if (text.StartsWith("/sync", StringComparison.OrdinalIgnoreCase))
        {
            var runner = scope.ServiceProvider.GetRequiredService<IManualSyncRunner>();
            await SendWithKeyboardAsync(bot, chatId, "Running ingests...", cancellationToken: ct);
            try
            {
                var result = await runner.RunAllAsync(ct);
                var summary = $"Sync finished in {(result.FinishedAt - result.StartedAt).TotalSeconds:0.#}s. " +
                              $"Updaters: {result.UpdatersRun}, Failures: {result.Failures}.";
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(summary + "\n" + string.Join("\n", result.Messages)), cancellationToken: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host is shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to run /sync");
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), cancellationToken: ct);
            }
            return;
        }

        await SendWithKeyboardAsync(bot, chatId, "Unknown command. Use /daily, /weekly, /add <text>", cancellationToken: ct);
    }

    private static ReplyKeyboardMarkup BuildKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("/daily"), new KeyboardButton("/weekly") },
            new[] { new KeyboardButton("/add ") }
        })
        {
            ResizeKeyboard = true
        };
    }

    private static Task SendWithKeyboardAsync(ITelegramBotClient bot, long chatId, string text, CancellationToken cancellationToken)
    {
        return bot.SendTextMessageAsync(chatId, text, replyMarkup: BuildKeyboard(), cancellationToken: cancellationToken);
    }

    private static Task<List<ContextItem>> GetDailyItemsAsync(ContextService contextService, TimeZoneInfo tz, CancellationToken ct)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);

        var localStart = new DateTime(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, tz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(localStart.AddDays(1), tz);

        // Pull enough rows to avoid a single source starving others.
        return contextService.GetForWindowAsync(new DateTimeOffset(startUtc, TimeSpan.Zero), new DateTimeOffset(endUtc, TimeSpan.Zero), take: 300, ct: ct);
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

        return contextService.GetForWindowAsync(new DateTimeOffset(weekStartUtc, TimeSpan.Zero), new DateTimeOffset(weekEndUtc, TimeSpan.Zero), take: 500, ct: ct);
    }

    private static string TruncateForTelegram(string text, int maxLen = 3500)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
        {
            return text;
        }

        return text[..maxLen] + "\n\n(truncated)";
    }

    private static string BuildUserFacingError(Exception ex)
    {
        // Keep this short; details are in logs.
        var message = ex.Message;
        if (message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            return "Summary failed: AI quota exceeded / billing issue (HTTP 429).";
        }

        if (message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "Summary failed: AI authentication error (check AI_API_KEY).";
        }

        return $"Summary failed: {message}";
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram bot error");
        return Task.CompletedTask;
    }
}
