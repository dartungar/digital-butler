using DigitalButler.Common;
using DigitalButler.Telegram.State;
using DigitalButler.Telegram.UI;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace DigitalButler.Telegram.Handlers;

public sealed class PhotoMessageHandler : IPhotoMessageHandler
{
    private static readonly Regex ObsidianPrefixRegex = new(
        @"^(?:please\s+)?(?:add|save|put)\s+(?:this\s+)?(?:note|message|text|item|photo|picture|image)?\s*(?:to|in|into)?\s*obsidian\s*[:\-–]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<PhotoMessageHandler> _logger;
    private readonly long _allowedUserId;
    private readonly ConversationStateManager _stateManager;

    public PhotoMessageHandler(
        ILogger<PhotoMessageHandler> logger,
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
        if (message.Photo is null || message.Photo.Length == 0)
            return;

        var chatId = message.Chat.Id;
        var userId = message.From?.Id;
        var caption = message.Caption;

        // Authorization check
        if (userId != _allowedUserId)
        {
            _logger.LogWarning("Unauthorized photo message from user {UserId}", userId);
            await SendWithKeyboardAsync(bot, chatId, "Unauthorized.", ct);
            return;
        }

        try
        {
            // Get largest photo
            var largestPhoto = message.Photo.OrderByDescending(p => p.FileSize).First();

            await PromptIncomingActionAsync(bot, chatId, caption, largestPhoto.FileId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process photo message");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task PromptIncomingActionAsync(
        ITelegramBotClient bot,
        long chatId,
        string? caption,
        string photoFileId,
        CancellationToken ct)
    {
        _stateManager.SetPendingIncomingChoice(chatId, new PendingIncomingChoice
        {
            Kind = PendingIncomingKind.Photo,
            TelegramFileId = photoFileId,
            CaptionOrText = caption,
            MediaFileExtension = ".jpg",
            CreatedAt = DateTimeOffset.UtcNow
        });
        _stateManager.ClearPendingObsidianCapture(chatId);
        _stateManager.ClearAwaitingObsidianDate(chatId);

        await bot.SendTextMessageAsync(chatId,
            "Should I try to find a Butler skill for this image, or add it to Obsidian?",
            replyMarkup: KeyboardFactory.BuildIncomingActionKeyboard(),
            cancellationToken: ct);
    }

    private static string? CleanObsidianText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        var cleaned = ObsidianPrefixRegex.Replace(trimmed, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleaned) && string.Equals(cleaned, trimmed, StringComparison.Ordinal))
        {
            return trimmed;
        }

        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
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
