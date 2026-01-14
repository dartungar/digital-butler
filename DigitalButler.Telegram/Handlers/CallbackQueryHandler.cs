using DigitalButler.Telegram.Skills;
using DigitalButler.Telegram.State;
using DigitalButler.Telegram.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace DigitalButler.Telegram.Handlers;

public sealed class CallbackQueryHandler : ICallbackQueryHandler
{
    private readonly ILogger<CallbackQueryHandler> _logger;
    private readonly long _allowedUserId;
    private readonly ConversationStateManager _stateManager;
    private readonly ISummarySkillExecutor _summaryExecutor;
    private readonly IMotivationSkillExecutor _motivationExecutor;
    private readonly IActivitiesSkillExecutor _activitiesExecutor;
    private readonly IDrawingReferenceSkillExecutor _drawingExecutor;
    private readonly ICalendarEventSkillExecutor _calendarExecutor;

    public CallbackQueryHandler(
        ILogger<CallbackQueryHandler> logger,
        IConfiguration config,
        ConversationStateManager stateManager,
        ISummarySkillExecutor summaryExecutor,
        IMotivationSkillExecutor motivationExecutor,
        IActivitiesSkillExecutor activitiesExecutor,
        IDrawingReferenceSkillExecutor drawingExecutor,
        ICalendarEventSkillExecutor calendarExecutor)
    {
        _logger = logger;
        _stateManager = stateManager;
        _summaryExecutor = summaryExecutor;
        _motivationExecutor = motivationExecutor;
        _activitiesExecutor = activitiesExecutor;
        _drawingExecutor = drawingExecutor;
        _calendarExecutor = calendarExecutor;

        var allowedUserIdStr = config["TELEGRAM_ALLOWED_USER_ID"];
        _allowedUserId = string.IsNullOrWhiteSpace(allowedUserIdStr)
            ? throw new InvalidOperationException("TELEGRAM_ALLOWED_USER_ID not configured")
            : long.Parse(allowedUserIdStr);
    }

    public async Task HandleAsync(ITelegramBotClient bot, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = callbackQuery.Message?.Chat.Id;
        var userId = callbackQuery.From.Id;
        var data = callbackQuery.Data;

        if (chatId is null || string.IsNullOrWhiteSpace(data))
        {
            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        // Authorization check
        if (userId != _allowedUserId)
        {
            _logger.LogWarning("Unauthorized callback query from user {UserId}", userId);
            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Unauthorized.", cancellationToken: ct);
            return;
        }

        // Route callback based on prefix
        if (data.StartsWith("help:", StringComparison.Ordinal))
        {
            await HandleHelpCallbackAsync(bot, callbackQuery, chatId.Value, data, ct);
            return;
        }

        if (data.StartsWith("summary:", StringComparison.Ordinal))
        {
            await HandleSummaryCallbackAsync(bot, callbackQuery, chatId.Value, data, ct);
            return;
        }

        if (data.StartsWith("motivation:", StringComparison.Ordinal))
        {
            await HandleMotivationCallbackAsync(bot, callbackQuery, chatId.Value, data, ct);
            return;
        }

        if (data.StartsWith("activities:", StringComparison.Ordinal))
        {
            await HandleActivitiesCallbackAsync(bot, callbackQuery, chatId.Value, data, ct);
            return;
        }

        if (data.StartsWith("drawref:", StringComparison.Ordinal))
        {
            await HandleDrawingReferenceCallbackAsync(bot, callbackQuery, chatId.Value, data, ct);
            return;
        }

        if (data.StartsWith("calevent:", StringComparison.Ordinal))
        {
            await HandleCalendarEventCallbackAsync(bot, callbackQuery, chatId.Value, data, ct);
            return;
        }

        // Unknown callback
        await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);
    }

    private async Task HandleHelpCallbackAsync(ITelegramBotClient bot, CallbackQuery callbackQuery, long chatId, string data, CancellationToken ct)
    {
        var action = data["help:".Length..];
        await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

        switch (action)
        {
            case "daily":
                await SendProcessingAndExecuteAsync(bot, chatId, "Generating daily summary...", async () =>
                {
                    var summary = await _summaryExecutor.ExecuteAsync(false, "on-demand-daily", ct);
                    return (TruncateForTelegram(summary ?? "No summary available."), KeyboardFactory.BuildSummaryRefreshKeyboard(false));
                }, ct);
                break;

            case "weekly":
                await SendProcessingAndExecuteAsync(bot, chatId, "Generating weekly summary...", async () =>
                {
                    var summary = await _summaryExecutor.ExecuteAsync(true, "on-demand-weekly", ct);
                    return (TruncateForTelegram(summary ?? "No summary available."), KeyboardFactory.BuildSummaryRefreshKeyboard(true));
                }, ct);
                break;

            case "motivation":
                await SendProcessingAndExecuteAsync(bot, chatId, "Generating motivation...", async () =>
                {
                    var result = await _motivationExecutor.ExecuteAsync(ct);
                    return (TruncateForTelegram(result ?? "No motivation available."), KeyboardFactory.BuildMotivationRefreshKeyboard());
                }, ct);
                break;

            case "activities":
                await SendProcessingAndExecuteAsync(bot, chatId, "Generating activities...", async () =>
                {
                    var result = await _activitiesExecutor.ExecuteAsync(ct);
                    return (TruncateForTelegram(result ?? "No activities available."), KeyboardFactory.BuildActivitiesRefreshKeyboard());
                }, ct);
                break;

            case "drawref":
                var randomTopic = _drawingExecutor.GetRandomTopic();
                _stateManager.SetPendingDrawingTopic(chatId, randomTopic);
                await bot.SendTextMessageAsync(chatId,
                    $"How about drawing: \"{randomTopic}\"?",
                    replyMarkup: KeyboardFactory.BuildDrawingTopicConfirmationKeyboard(),
                    cancellationToken: ct);
                break;

            case "addevent":
                await SendWithKeyboardAsync(bot, chatId, "To add an event, type:\n/addevent Meeting with John tomorrow at 3pm", ct);
                break;
        }
    }

    private async Task HandleSummaryCallbackAsync(ITelegramBotClient bot, CallbackQuery callbackQuery, long chatId, string data, CancellationToken ct)
    {
        var action = data["summary:".Length..];
        await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

        var isWeekly = action is "weekly" or "refresh_weekly";

        await SendProcessingAndExecuteAsync(bot, chatId, isWeekly ? "Generating weekly summary..." : "Generating daily summary...", async () =>
        {
            var taskName = isWeekly ? "on-demand-weekly" : "on-demand-daily";
            var summary = await _summaryExecutor.ExecuteAsync(isWeekly, taskName, ct);
            return (TruncateForTelegram(summary ?? "No summary available."), KeyboardFactory.BuildSummaryRefreshKeyboard(isWeekly));
        }, ct);
    }

    private async Task HandleMotivationCallbackAsync(ITelegramBotClient bot, CallbackQuery callbackQuery, long chatId, string data, CancellationToken ct)
    {
        await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

        await SendProcessingAndExecuteAsync(bot, chatId, "Generating motivation...", async () =>
        {
            var result = await _motivationExecutor.ExecuteAsync(ct);
            return (TruncateForTelegram(result ?? "No motivation available."), KeyboardFactory.BuildMotivationRefreshKeyboard());
        }, ct);
    }

    private async Task HandleActivitiesCallbackAsync(ITelegramBotClient bot, CallbackQuery callbackQuery, long chatId, string data, CancellationToken ct)
    {
        await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

        await SendProcessingAndExecuteAsync(bot, chatId, "Generating activities...", async () =>
        {
            var result = await _activitiesExecutor.ExecuteAsync(ct);
            return (TruncateForTelegram(result ?? "No activities available."), KeyboardFactory.BuildActivitiesRefreshKeyboard());
        }, ct);
    }

    private async Task HandleDrawingReferenceCallbackAsync(ITelegramBotClient bot, CallbackQuery callbackQuery, long chatId, string data, CancellationToken ct)
    {
        var action = data["drawref:".Length..];

        if (action == "confirm")
        {
            var topic = _stateManager.GetAndRemovePendingDrawingTopic(chatId);
            if (topic is null)
            {
                await bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Session expired. Please try again.", cancellationToken: ct);
                return;
            }

            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

            // Edit the original message
            if (callbackQuery.Message is not null)
            {
                await bot.EditMessageTextAsync(chatId, callbackQuery.Message.MessageId,
                    $"Drawing topic: {topic}",
                    replyMarkup: null,
                    cancellationToken: ct);
            }

            await SendWithKeyboardAsync(bot, chatId, "Finding a drawing reference...", ct);

            try
            {
                _stateManager.SetLastDrawingSubject(chatId, topic);
                var reply = await _drawingExecutor.ExecuteAsync(topic, ct);
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
        else if (action == "another")
        {
            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

            var newTopic = _drawingExecutor.GetRandomTopic();
            _stateManager.SetPendingDrawingTopic(chatId, newTopic);

            if (callbackQuery.Message is not null)
            {
                await bot.EditMessageTextAsync(chatId, callbackQuery.Message.MessageId,
                    $"How about drawing: \"{newTopic}\"?",
                    replyMarkup: KeyboardFactory.BuildDrawingTopicConfirmationKeyboard(),
                    cancellationToken: ct);
            }
        }
        else if (action == "different_image")
        {
            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

            var lastSubject = _stateManager.GetLastDrawingSubject(chatId);
            if (string.IsNullOrWhiteSpace(lastSubject))
            {
                await SendWithKeyboardAsync(bot, chatId, "No previous subject found. Try /drawref <subject>", ct);
                return;
            }

            await SendWithKeyboardAsync(bot, chatId, "Finding another reference...", ct);

            try
            {
                var reply = await _drawingExecutor.ExecuteAsync(lastSubject, ct);
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
        else if (action == "different_subject")
        {
            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

            var newTopic = _drawingExecutor.GetRandomTopic();
            _stateManager.SetPendingDrawingTopic(chatId, newTopic);

            await bot.SendTextMessageAsync(chatId,
                $"How about drawing: \"{newTopic}\"?",
                replyMarkup: KeyboardFactory.BuildDrawingTopicConfirmationKeyboard(),
                cancellationToken: ct);
        }
    }

    private async Task HandleCalendarEventCallbackAsync(ITelegramBotClient bot, CallbackQuery callbackQuery, long chatId, string data, CancellationToken ct)
    {
        var action = data["calevent:".Length..];

        if (action == "confirm")
        {
            var pending = _stateManager.GetAndRemovePendingCalendarEvent(chatId);
            if (pending is null)
            {
                await bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Session expired. Please try again.", cancellationToken: ct);
                return;
            }

            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

            // Edit the original message
            if (callbackQuery.Message is not null)
            {
                await bot.EditMessageTextAsync(chatId, callbackQuery.Message.MessageId,
                    $"Creating event: {pending.Title}...",
                    replyMarkup: null,
                    cancellationToken: ct);
            }

            var result = await _calendarExecutor.CreateEventAsync(pending, ct);

            if (result.Success)
            {
                await SendWithKeyboardAsync(bot, chatId,
                    $"Event created: {pending.Title}\n{result.HtmlLink}",
                    ct);
            }
            else
            {
                await SendWithKeyboardAsync(bot, chatId,
                    $"Failed to create event: {result.Error}",
                    ct);
            }
        }
        else if (action == "reject")
        {
            _stateManager.ClearPendingCalendarEvent(chatId);
            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

            if (callbackQuery.Message is not null)
            {
                await bot.EditMessageTextAsync(chatId, callbackQuery.Message.MessageId,
                    "Event creation cancelled.",
                    replyMarkup: null,
                    cancellationToken: ct);
            }
        }
    }

    private async Task SendProcessingAndExecuteAsync(
        ITelegramBotClient bot,
        long chatId,
        string processingMessage,
        Func<Task<(string text, global::Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup? keyboard)>> execute,
        CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, processingMessage, ct);

        try
        {
            var (text, keyboard) = await execute();
            await bot.SendTextMessageAsync(chatId, text, replyMarkup: keyboard, cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute callback action");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
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
