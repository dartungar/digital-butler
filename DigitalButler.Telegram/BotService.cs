using DigitalButler.Common;
using DigitalButler.Telegram.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Polling = Telegram.Bot.Polling;
using Telegram.Bot.Types;
using System.Diagnostics;

namespace DigitalButler.Telegram;

public class BotService : IHostedService, IDisposable
{
    private readonly ILogger<BotService> _logger;
    private readonly IServiceProvider _services;
    private readonly string _token;
    private readonly TimeSpan _startupPingTimeout;
    private readonly bool _forceIpv4;
    private readonly ITelegramErrorNotifier? _errorNotifier;

    private TelegramBotClient? _bot;
    private CancellationTokenSource? _cts;
    private Task? _runLoop;

    public BotService(
        ILogger<BotService> logger,
        IServiceProvider services,
        IConfiguration config,
        ITelegramErrorNotifier? errorNotifier = null)
    {
        _logger = logger;
        _services = services;
        _errorNotifier = errorNotifier;

        _token = config["TELEGRAM_BOT_TOKEN"] ?? throw new InvalidOperationException("TELEGRAM_BOT_TOKEN not configured");

        var timeoutSeconds = 30;
        var timeoutStr = config["TELEGRAM_STARTUP_TIMEOUT_SECONDS"];
        if (!string.IsNullOrWhiteSpace(timeoutStr) && int.TryParse(timeoutStr, out var parsed) && parsed > 0)
        {
            timeoutSeconds = parsed;
        }
        _startupPingTimeout = TimeSpan.FromSeconds(timeoutSeconds);

        var forceIpv4Str = config["TELEGRAM_FORCE_IPV4"];
        _forceIpv4 = !string.IsNullOrWhiteSpace(forceIpv4Str) &&
                 (forceIpv4Str.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                  forceIpv4Str.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                  forceIpv4Str.Equals("yes", StringComparison.OrdinalIgnoreCase));
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
            CancellationTokenSource? pingCts = null;
            var sw = Stopwatch.StartNew();

            try
            {
                _bot = TelegramBotClientFactory.Create(_token, _forceIpv4);

                // Avoid hanging forever on startup (TLS/network issues). Keep it short and retry.
                pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pingCts.CancelAfter(_startupPingTimeout);

                var me = await _bot.GetMeAsync(cancellationToken: pingCts.Token);
                _logger.LogInformation(
                    "Bot connected as {Username} (forceIpv4={ForceIpv4}, elapsedMs={ElapsedMs})",
                    me.Username,
                    _forceIpv4,
                    (long)sw.Elapsed.TotalMilliseconds);

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
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                var pingTimedOut = pingCts?.IsCancellationRequested == true;
                var innermost = GetInnermostException(ex);

                _logger.LogWarning(
                    "Telegram bot startup ping cancelled/timed out after {TimeoutSeconds}s; retrying in {Delay}. " +
                    "forceIpv4={ForceIpv4}, elapsedMs={ElapsedMs}, pingTimedOut={PingTimedOut}, " +
                    "error={ErrorType}, message={Message}, inner={InnerType}: {InnerMessage}",
                    (int)_startupPingTimeout.TotalSeconds,
                    delay,
                    _forceIpv4,
                    (long)sw.Elapsed.TotalMilliseconds,
                    pingTimedOut,
                    ex.GetType().Name,
                    ex.Message,
                    innermost?.GetType().Name,
                    innermost?.Message);

                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }

                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
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

                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
            finally
            {
                pingCts?.Dispose();
            }
        }
    }

    private static Exception? GetInnermostException(Exception ex)
    {
        var cur = ex.InnerException;
        while (cur?.InnerException is not null)
        {
            cur = cur.InnerException;
        }
        return cur;
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            // Create a scope for each update to properly resolve scoped services
            using var scope = _services.CreateScope();

            // Handle callback queries (inline keyboard interactions)
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is not null)
            {
                var callbackHandler = scope.ServiceProvider.GetRequiredService<ICallbackQueryHandler>();
                await callbackHandler.HandleAsync(bot, update.CallbackQuery, ct);
                return;
            }

            if (update.Type != UpdateType.Message || update.Message is null)
                return;

            // Handle voice messages
            if (update.Message.Voice is not null)
            {
                var voiceHandler = scope.ServiceProvider.GetRequiredService<IVoiceMessageHandler>();
                await voiceHandler.HandleAsync(bot, update.Message, ct);
                return;
            }

            // Handle photo messages
            if (update.Message.Photo is not null && update.Message.Photo.Length > 0)
            {
                var photoHandler = scope.ServiceProvider.GetRequiredService<IPhotoMessageHandler>();
                await photoHandler.HandleAsync(bot, update.Message, ct);
                return;
            }

            // Handle text messages
            if (update.Message.Text is not null)
            {
                var textHandler = scope.ServiceProvider.GetRequiredService<ITextMessageHandler>();
                await textHandler.HandleAsync(bot, update.Message, ct);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in update handler");
            if (_errorNotifier != null)
            {
                await _errorNotifier.NotifyErrorAsync("Message handler", ex, ct);
            }
        }
    }

    private async Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram bot error");
        if (_errorNotifier != null)
        {
            await _errorNotifier.NotifyErrorAsync("Telegram bot polling", exception, cancellationToken);
        }
    }
}
