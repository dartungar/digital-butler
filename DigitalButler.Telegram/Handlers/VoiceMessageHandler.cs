using DigitalButler.Common;
using DigitalButler.Telegram.State;
using DigitalButler.Telegram.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace DigitalButler.Telegram.Handlers;

public sealed class VoiceMessageHandler : IVoiceMessageHandler
{
    private readonly ILogger<VoiceMessageHandler> _logger;
    private readonly long _allowedUserId;
    private readonly ConversationStateManager _stateManager;

    public VoiceMessageHandler(
        ILogger<VoiceMessageHandler> logger,
        IConfiguration config,
        ConversationStateManager stateManager)
    {
        _logger = logger;
        _stateManager = stateManager;

        var allowedUserIdStr = config["TELEGRAM_ALLOWED_USER_ID"];
        _allowedUserId = string.IsNullOrWhiteSpace(allowedUserIdStr)
            ? throw new InvalidOperationException("TELEGRAM_ALLOWED_USER_ID not configured")
            : long.Parse(allowedUserIdStr);
    }

    public async Task HandleAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        if (message.Voice is null)
            return;

        var chatId = message.Chat.Id;
        var userId = message.From?.Id;

        // Authorization check
        if (userId != _allowedUserId)
        {
            _logger.LogWarning("Unauthorized voice message from user {UserId}", userId);
            await SendWithKeyboardAsync(bot, chatId, "Unauthorized.", ct);
            return;
        }

        try
        {
            await PromptIncomingActionAsync(bot, chatId, message.Voice.FileId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process voice message");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task PromptIncomingActionAsync(ITelegramBotClient bot, long chatId, string voiceFileId, CancellationToken ct)
    {
        _stateManager.SetPendingIncomingChoice(chatId, new PendingIncomingChoice
        {
            Kind = PendingIncomingKind.Voice,
            TelegramFileId = voiceFileId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        _stateManager.ClearPendingObsidianCapture(chatId);
        _stateManager.ClearAwaitingObsidianDate(chatId);

        await bot.SendTextMessageAsync(chatId,
            "Should I try to find a Butler skill for this voice note, or add it to Obsidian?",
            replyMarkup: KeyboardFactory.BuildIncomingActionKeyboard(),
            cancellationToken: ct);
    }

    private static Task SendWithKeyboardAsync(ITelegramBotClient bot, long chatId, string text, CancellationToken ct)
    {
        return bot.SendTextMessageAsync(chatId, text, replyMarkup: KeyboardFactory.BuildMainReplyKeyboard(), cancellationToken: ct);
    }

    private static string TruncateForTelegram(string text, int maxLen = 3500)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
            return text;
        return text[..maxLen] + "\n\n(truncated)";
    }

    private static string BuildUserFacingError(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            return "Request failed: AI quota exceeded / billing issue (HTTP 429).";
        }
        if (message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "Request failed: AI authentication error (check AI_API_KEY).";
        }
        return $"Request failed: {message}";
    }
}
