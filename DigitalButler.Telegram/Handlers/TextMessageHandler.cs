using DigitalButler.Common;
using DigitalButler.Context;
using DigitalButler.Skills;
using DigitalButler.Telegram.Skills;
using DigitalButler.Telegram.State;
using DigitalButler.Telegram.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace DigitalButler.Telegram.Handlers;

public sealed class TextMessageHandler : ITextMessageHandler
{
    private readonly ILogger<TextMessageHandler> _logger;
    private readonly long _allowedUserId;
    private readonly ConversationStateManager _stateManager;
    private readonly ContextService _contextService;
    private readonly ISkillRouter _skillRouter;
    private readonly ISummarySkillExecutor _summaryExecutor;
    private readonly IMotivationSkillExecutor _motivationExecutor;
    private readonly IActivitiesSkillExecutor _activitiesExecutor;
    private readonly IDrawingReferenceSkillExecutor _drawingExecutor;
    private readonly ICalendarEventSkillExecutor _calendarExecutor;
    private readonly IManualSyncRunner _syncRunner;

    public TextMessageHandler(
        ILogger<TextMessageHandler> logger,
        IConfiguration config,
        ConversationStateManager stateManager,
        ContextService contextService,
        ISkillRouter skillRouter,
        ISummarySkillExecutor summaryExecutor,
        IMotivationSkillExecutor motivationExecutor,
        IActivitiesSkillExecutor activitiesExecutor,
        IDrawingReferenceSkillExecutor drawingExecutor,
        ICalendarEventSkillExecutor calendarExecutor,
        IManualSyncRunner syncRunner)
    {
        _logger = logger;
        _stateManager = stateManager;
        _contextService = contextService;
        _skillRouter = skillRouter;
        _summaryExecutor = summaryExecutor;
        _motivationExecutor = motivationExecutor;
        _activitiesExecutor = activitiesExecutor;
        _drawingExecutor = drawingExecutor;
        _calendarExecutor = calendarExecutor;
        _syncRunner = syncRunner;

        var allowedUserIdStr = config["TELEGRAM_ALLOWED_USER_ID"];
        _allowedUserId = string.IsNullOrWhiteSpace(allowedUserIdStr)
            ? throw new InvalidOperationException("TELEGRAM_ALLOWED_USER_ID not configured")
            : long.Parse(allowedUserIdStr);
    }

    public async Task HandleAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        if (message.Text is null)
            return;

        var chatId = message.Chat.Id;
        var userId = message.From?.Id;
        var text = message.Text.Trim();

        // Authorization check
        if (userId != _allowedUserId)
        {
            _logger.LogWarning("Unauthorized access attempt from user {UserId}", userId);
            await SendWithKeyboardAsync(bot, chatId, "Unauthorized.", ct);
            return;
        }

        // Check if awaiting drawing subject
        if (_stateManager.IsAwaitingDrawingSubject(chatId) && !text.StartsWith("/", StringComparison.OrdinalIgnoreCase))
        {
            _stateManager.ClearAwaitingDrawingSubject(chatId);
            await HandleDrawingReferenceAsync(bot, chatId, text, ct);
            return;
        }

        // Command handling
        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await HandleHelpCommandAsync(bot, chatId, ct);
            return;
        }

        if (text.StartsWith("/drawref", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/drawingref", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/drawing_reference", StringComparison.OrdinalIgnoreCase))
        {
            var subject = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(subject))
            {
                await SuggestRandomDrawingTopicAsync(bot, chatId, ct);
                return;
            }
            await HandleDrawingReferenceAsync(bot, chatId, subject, ct);
            return;
        }

        if (text.StartsWith("/addevent", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/newevent", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/event ", StringComparison.OrdinalIgnoreCase))
        {
            var eventText = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(eventText))
            {
                await SendWithKeyboardAsync(bot, chatId, "Usage: /addevent Meeting with John tomorrow at 3pm", ct);
                return;
            }
            await HandleAddEventAsync(bot, chatId, eventText, ct);
            return;
        }

        if (text.StartsWith("/add", StringComparison.OrdinalIgnoreCase))
        {
            var content = text[4..].Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                await SendWithKeyboardAsync(bot, chatId, "Usage: /add your note", ct);
                return;
            }
            await _contextService.AddPersonalAsync(content, ct: ct);
            await SendWithKeyboardAsync(bot, chatId, "Saved", ct);
            return;
        }

        if (text.StartsWith("/summary", StringComparison.OrdinalIgnoreCase))
        {
            // Show choice keyboard for summary type
            await bot.SendTextMessageAsync(chatId, "Which summary would you like?",
                replyMarkup: KeyboardFactory.BuildSummaryChoiceKeyboard(),
                cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/daily", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSummaryAsync(bot, chatId, weekly: false, ct);
            return;
        }

        if (text.StartsWith("/weekly", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSummaryAsync(bot, chatId, weekly: true, ct);
            return;
        }

        if (text.StartsWith("/sync", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSyncAsync(bot, chatId, ct);
            return;
        }

        if (text.StartsWith("/motivation", StringComparison.OrdinalIgnoreCase))
        {
            await HandleMotivationAsync(bot, chatId, userQuery: null, ct);
            return;
        }

        if (text.StartsWith("/activities", StringComparison.OrdinalIgnoreCase))
        {
            await HandleActivitiesAsync(bot, chatId, ct);
            return;
        }

        // Plain text: route to skill using AI
        if (!text.StartsWith("/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSkillRoutingAsync(bot, chatId, text, ct);
            return;
        }

        await SendWithKeyboardAsync(bot, chatId, "Unknown command. Use /help to see available commands.", ct);
    }

    private async Task HandleHelpCommandAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        var helpText = "What would you like to do?";
        await bot.SendTextMessageAsync(chatId, helpText,
            replyMarkup: KeyboardFactory.BuildHelpInlineKeyboard(),
            cancellationToken: ct);
    }

    private async Task HandleSummaryAsync(ITelegramBotClient bot, long chatId, bool weekly, CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, weekly ? "Generating weekly summary..." : "Generating daily summary...", ct);
        try
        {
            var taskName = weekly ? "on-demand-weekly" : "on-demand-daily";
            var summary = await _summaryExecutor.ExecuteAsync(weekly, taskName, ct);
            if (string.IsNullOrWhiteSpace(summary)) summary = "No summary available.";

            await bot.SendTextMessageAsync(chatId, TruncateForTelegram(summary),
                replyMarkup: KeyboardFactory.BuildSummaryRefreshKeyboard(weekly),
                cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate summary");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task HandleMotivationAsync(ITelegramBotClient bot, long chatId, string? userQuery, CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, "Generating motivation...", ct);
        try
        {
            var result = await _motivationExecutor.ExecuteAsync(userQuery, ct);
            if (string.IsNullOrWhiteSpace(result)) result = "No motivation available.";

            await bot.SendTextMessageAsync(chatId, TruncateForTelegram(result),
                replyMarkup: KeyboardFactory.BuildMotivationRefreshKeyboard(),
                cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate motivation");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task HandleActivitiesAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, "Generating activities...", ct);
        try
        {
            var result = await _activitiesExecutor.ExecuteAsync(ct);
            if (string.IsNullOrWhiteSpace(result)) result = "No activities available.";

            await bot.SendTextMessageAsync(chatId, TruncateForTelegram(result),
                replyMarkup: KeyboardFactory.BuildActivitiesRefreshKeyboard(),
                cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate activities");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task HandleDrawingReferenceAsync(ITelegramBotClient bot, long chatId, string subject, CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, "Finding a drawing reference...", ct);
        try
        {
            _stateManager.SetLastDrawingSubject(chatId, subject);
            var reply = await _drawingExecutor.ExecuteAsync(subject, ct);

            await bot.SendTextMessageAsync(chatId, TruncateForTelegram(reply),
                replyMarkup: KeyboardFactory.BuildDrawingResultKeyboard(),
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate drawing reference");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task SuggestRandomDrawingTopicAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        var randomTopic = _drawingExecutor.GetRandomTopic();
        _stateManager.SetPendingDrawingTopic(chatId, randomTopic);

        await bot.SendTextMessageAsync(chatId,
            $"How about drawing: \"{randomTopic}\"?",
            replyMarkup: KeyboardFactory.BuildDrawingTopicConfirmationKeyboard(),
            cancellationToken: ct);
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

        try
        {
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse calendar event");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task HandleSyncAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, "Running ingests...", ct);
        try
        {
            var result = await _syncRunner.RunAllAsync(ct);
            var summary = $"Sync finished in {(result.FinishedAt - result.StartedAt).TotalSeconds:0.#}s. " +
                          $"Updaters: {result.UpdatersRun}, Failures: {result.Failures}.";
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(summary + "\n" + string.Join("\n", result.Messages)), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to run sync");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task HandleSkillRoutingAsync(ITelegramBotClient bot, long chatId, string text, CancellationToken ct)
    {
        var route = await _skillRouter.RouteAsync(text, ct);
        switch (route.Skill)
        {
            case ButlerSkill.Motivation:
                await HandleMotivationAsync(bot, chatId, userQuery: text, ct);
                break;
            case ButlerSkill.Activities:
                await HandleActivitiesAsync(bot, chatId, ct);
                break;
            case ButlerSkill.DrawingReference:
                if (!DrawingReferenceSkillExecutor.TryExtractSubject(text, out var extracted))
                {
                    await SuggestRandomDrawingTopicAsync(bot, chatId, ct);
                    return;
                }
                await HandleDrawingReferenceAsync(bot, chatId, extracted!, ct);
                break;
            case ButlerSkill.CalendarEvent:
                var eventText = CalendarEventSkillExecutor.TryExtractEventText(text) ?? text;
                await HandleAddEventAsync(bot, chatId, eventText, ct);
                break;
            case ButlerSkill.DailySummary:
                await HandleSummaryAsync(bot, chatId, weekly: false, ct);
                break;
            case ButlerSkill.WeeklySummary:
                await HandleSummaryAsync(bot, chatId, weekly: true, ct);
                break;
            default:
                await HandleSummaryAsync(bot, chatId, weekly: false, ct);
                break;
        }
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
