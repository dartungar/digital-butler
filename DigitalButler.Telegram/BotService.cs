using DigitalButler.Modules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
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

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await bot.SendTextMessageAsync(chatId, "Commands:\n/summary - get current summary\n/add <text> - add personal context\n/sync - run all ingests now", cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/add", StringComparison.OrdinalIgnoreCase))
        {
            var content = text[4..].Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                await bot.SendTextMessageAsync(chatId, "Usage: /add your note", cancellationToken: ct);
                return;
            }

            await contextService.AddPersonalAsync(content, ct: ct);
            await bot.SendTextMessageAsync(chatId, "Saved", cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/summary", StringComparison.OrdinalIgnoreCase))
        {
            var items = await contextService.GetRelevantAsync(daysBack: 7, take: 100, ct: ct);
            var sources = items.Select(x => x.Source).Distinct().ToArray();
            var instructionsBySource = await instructionService.GetBySourcesAsync(sources, ct);
            var summary = await summarizer.SummarizeAsync(items, instructionsBySource, "on-demand", ct);
            if (string.IsNullOrWhiteSpace(summary)) summary = "No summary available.";
            await bot.SendTextMessageAsync(chatId, summary, cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/sync", StringComparison.OrdinalIgnoreCase))
        {
            var runner = scope.ServiceProvider.GetRequiredService<IManualSyncRunner>();
            await bot.SendTextMessageAsync(chatId, "Running ingests...", cancellationToken: ct);
            var result = await runner.RunAllAsync(ct);
            var summary = $"Sync finished in {(result.FinishedAt - result.StartedAt).TotalSeconds:0.#}s. " +
                          $"Updaters: {result.UpdatersRun}, Failures: {result.Failures}.";
            await bot.SendTextMessageAsync(chatId, summary + "\n" + string.Join("\n", result.Messages), cancellationToken: ct);
            return;
        }

        await bot.SendTextMessageAsync(chatId, "Unknown command. Use /summary or /add <text>", cancellationToken: ct);
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram bot error");
        return Task.CompletedTask;
    }
}
