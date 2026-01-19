using DigitalButler.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

namespace DigitalButler.Telegram;

public class TelegramErrorNotifier : ITelegramErrorNotifier
{
    private readonly ITelegramBotClient? _bot;
    private readonly string? _chatId;
    private readonly ILogger<TelegramErrorNotifier> _logger;

    public TelegramErrorNotifier(
        ILogger<TelegramErrorNotifier> logger,
        IConfiguration config,
        ITelegramBotClient? bot = null)
    {
        _logger = logger;
        _bot = bot;
        _chatId = config["Telegram:ChatId"];
    }

    public async Task NotifyErrorAsync(string context, Exception ex, CancellationToken ct = default)
    {
        var message = FormatErrorMessage(context, ex.GetType().Name, ex.Message);
        await SendAsync(message, ct);
    }

    public async Task NotifyErrorAsync(string context, string message, CancellationToken ct = default)
    {
        var formatted = FormatErrorMessage(context, null, message);
        await SendAsync(formatted, ct);
    }

    private static string FormatErrorMessage(string context, string? exceptionType, string message)
    {
        var header = $"⚠️ Error: {context}";
        var body = exceptionType != null
            ? $"{exceptionType}: {message}"
            : message;

        // Truncate if too long for Telegram (4096 char limit)
        var full = $"{header}\n{body}";
        if (full.Length > 4000)
        {
            full = full[..4000] + "...";
        }

        return full;
    }

    private async Task SendAsync(string message, CancellationToken ct)
    {
        if (_bot == null || string.IsNullOrWhiteSpace(_chatId))
        {
            _logger.LogDebug("Cannot send error notification: bot is {BotStatus}, chatId is {ChatIdStatus}",
                _bot == null ? "null" : "available",
                string.IsNullOrWhiteSpace(_chatId) ? "missing" : "set");
            return;
        }

        try
        {
            await _bot.SendTextMessageAsync(_chatId, message, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Don't throw - error notification failures shouldn't crash the app
            _logger.LogWarning(ex, "Failed to send error notification to Telegram");
        }
    }
}
