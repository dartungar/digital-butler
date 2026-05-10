using DigitalButler.Common;
using DigitalButler.Skills;
using DigitalButler.Skills.VaultSearch;
using DigitalButler.Telegram.Skills;
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
    private readonly IMediaDownloadService _mediaDownloadService;
    private readonly IAudioTranscriptionService _transcriptionService;
    private readonly ISkillRouter _skillRouter;
    private readonly IDateQueryTranslator _dateTranslator;
    private readonly ISummarySkillExecutor _summaryExecutor;
    private readonly IMotivationSkillExecutor _motivationExecutor;
    private readonly IActivitiesSkillExecutor _activitiesExecutor;
    private readonly ICalendarEventSkillExecutor _calendarExecutor;
    private readonly IVaultSearchSkillExecutor _vaultSearchExecutor;
    private readonly ConversationStateManager _stateManager;

    public VoiceMessageHandler(
        ILogger<VoiceMessageHandler> logger,
        IConfiguration config,
        IMediaDownloadService mediaDownloadService,
        IAudioTranscriptionService transcriptionService,
        ISkillRouter skillRouter,
        IDateQueryTranslator dateTranslator,
        ISummarySkillExecutor summaryExecutor,
        IMotivationSkillExecutor motivationExecutor,
        IActivitiesSkillExecutor activitiesExecutor,
        ICalendarEventSkillExecutor calendarExecutor,
        IVaultSearchSkillExecutor vaultSearchExecutor,
        ConversationStateManager stateManager)
    {
        _logger = logger;
        _mediaDownloadService = mediaDownloadService;
        _transcriptionService = transcriptionService;
        _skillRouter = skillRouter;
        _dateTranslator = dateTranslator;
        _summaryExecutor = summaryExecutor;
        _motivationExecutor = motivationExecutor;
        _activitiesExecutor = activitiesExecutor;
        _calendarExecutor = calendarExecutor;
        _vaultSearchExecutor = vaultSearchExecutor;
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

        if (userId != _allowedUserId)
        {
            _logger.LogWarning("Unauthorized voice message from user {UserId}", userId);
            await SendWithKeyboardAsync(bot, chatId, "Unauthorized.", ct);
            return;
        }

        try
        {
            await SendWithKeyboardAsync(bot, chatId, "Transcribing voice message...", ct);
            var audioData = await _mediaDownloadService.DownloadFileAsync(bot, message.Voice.FileId, ct);
            var transcript = await _transcriptionService.TranscribeAsync(audioData, $"voice_{message.Voice.FileId}.ogg", ct);

            if (string.IsNullOrWhiteSpace(transcript.Text))
            {
                await SendWithKeyboardAsync(bot, chatId, "I couldn't transcribe this voice message.", ct);
                return;
            }

            await HandleSkillRoutingAsync(bot, chatId, transcript.Text.Trim(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process voice message");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task HandleSkillRoutingAsync(ITelegramBotClient bot, long chatId, string text, CancellationToken ct)
    {
        var routingResult = await _skillRouter.RouteWithEnrichmentAsync(text, ct);

        DateOnly? startDate = null;
        DateOnly? endDate = null;
        string? vaultQuery = null;

        if (routingResult.NeedsVaultEnrichment)
        {
            vaultQuery = routingResult.VaultSearchQuery ?? text;
            var translated = _dateTranslator.TranslateQuery(vaultQuery, DateTimeOffset.UtcNow);
            startDate = translated.StartDate;
            endDate = translated.EndDate;

            if (!startDate.HasValue && !endDate.HasValue)
            {
                translated = _dateTranslator.TranslateQuery(text, DateTimeOffset.UtcNow);
                startDate = translated.StartDate;
                endDate = translated.EndDate;
            }
        }

        switch (routingResult.Skill)
        {
            case ButlerSkill.Motivation:
                await HandleMotivationAsync(bot, chatId, text, vaultQuery, startDate, endDate, ct);
                break;
            case ButlerSkill.Activities:
                await HandleActivitiesAsync(bot, chatId, text, vaultQuery, startDate, endDate, ct);
                break;
            case ButlerSkill.CalendarEvent:
                var eventText = CalendarEventSkillExecutor.TryExtractEventText(text) ?? text;
                await HandleAddEventAsync(bot, chatId, eventText, ct);
                break;
            case ButlerSkill.VaultSearch:
                if (startDate.HasValue && endDate.HasValue)
                {
                    await HandleSummaryAsync(bot, chatId, false, vaultQuery, startDate, endDate, ct);
                }
                else
                {
                    await HandleVaultSearchAsync(bot, chatId, routingResult.VaultSearchQuery ?? text, ct);
                }

                break;
            case ButlerSkill.DailySummary:
                await HandleSummaryAsync(bot, chatId, false, vaultQuery, startDate, endDate, ct);
                break;
            case ButlerSkill.WeeklySummary:
                await HandleSummaryAsync(bot, chatId, true, vaultQuery, startDate, endDate, ct);
                break;
            default:
                await SendWithKeyboardAsync(bot, chatId, "I couldn't match that voice note to a Butler skill.", ct);
                break;
        }
    }

    private async Task HandleSummaryAsync(
        ITelegramBotClient bot,
        long chatId,
        bool weekly,
        string? vaultQuery,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, weekly ? "Generating weekly summary..." : "Generating summary...", ct);

        var taskName = weekly ? "on-demand-weekly" : "on-demand-daily";
        var summary = await _summaryExecutor.ExecuteAsync(weekly, taskName, vaultQuery, startDate, endDate, ct);
        if (string.IsNullOrWhiteSpace(summary)) summary = "No summary available.";

        await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(summary), ct);
    }

    private async Task HandleMotivationAsync(
        ITelegramBotClient bot,
        long chatId,
        string userQuery,
        string? vaultQuery,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, "Generating motivation...", ct);
        var result = await _motivationExecutor.ExecuteAsync(userQuery, vaultQuery, startDate, endDate, ct);
        if (string.IsNullOrWhiteSpace(result)) result = "No motivation available.";
        await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(result), ct);
    }

    private async Task HandleActivitiesAsync(
        ITelegramBotClient bot,
        long chatId,
        string userQuery,
        string? vaultQuery,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, "Generating activities...", ct);
        var result = await _activitiesExecutor.ExecuteAsync(userQuery, vaultQuery, startDate, endDate, ct);
        if (string.IsNullOrWhiteSpace(result)) result = "No activities available.";
        await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(result), ct);
    }

    private async Task HandleAddEventAsync(ITelegramBotClient bot, long chatId, string eventText, CancellationToken ct)
    {
        if (!_calendarExecutor.IsConfigured)
        {
            await SendWithKeyboardAsync(bot, chatId,
                "Google Calendar is not configured. Set GOOGLE_SERVICE_ACCOUNT_KEY_PATH or GOOGLE_SERVICE_ACCOUNT_JSON, and GOOGLE_CALENDAR_ID.",
                ct);
            return;
        }

        await SendWithKeyboardAsync(bot, chatId, "Parsing your event...", ct);

        var result = await _calendarExecutor.ParseEventAsync(eventText, ct);
        if (result is null)
        {
            await SendWithKeyboardAsync(bot, chatId,
                "I couldn't understand that event. Try something like:\n\"Meeting with John tomorrow at 3pm for 1 hour\"",
                ct);
            return;
        }

        _stateManager.SetPendingCalendarEvent(chatId, result.Parsed);

        await bot.SendTextMessageAsync(chatId, result.Preview,
            replyMarkup: KeyboardFactory.BuildEventConfirmationKeyboard(),
            cancellationToken: ct);
    }

    private async Task HandleVaultSearchAsync(ITelegramBotClient bot, long chatId, string query, CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, "Searching vault...", ct);
        var result = await _vaultSearchExecutor.ExecuteAsync(query, ct);
        await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(result), ct);
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
