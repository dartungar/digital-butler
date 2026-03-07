using DigitalButler.Skills;
using DigitalButler.Skills.VaultSearch;
using DigitalButler.Common;
using DigitalButler.Context;
using DigitalButler.Telegram.Skills;
using DigitalButler.Telegram.State;
using DigitalButler.Telegram.UI;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace DigitalButler.Telegram.Handlers;

public sealed class CallbackQueryHandler : ICallbackQueryHandler
{
    private static readonly Regex ObsidianPrefixRegex = new(
        @"^(?:please\s+)?(?:add|save|put)\s+(?:this\s+)?(?:note|message|text|item|photo|picture|image)?\s*(?:to|in|into)?\s*obsidian\s*[:\-–]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<CallbackQueryHandler> _logger;
    private readonly long _allowedUserId;
    private readonly ConversationStateManager _stateManager;
    private readonly IMediaDownloadService _mediaDownloadService;
    private readonly IAudioTranscriptionService _transcriptionService;
    private readonly IImageAnalysisService _imageAnalysisService;
    private readonly ISkillRouter _skillRouter;
    private readonly IDateQueryTranslator _dateTranslator;
    private readonly ISummarySkillExecutor _summaryExecutor;
    private readonly IMotivationSkillExecutor _motivationExecutor;
    private readonly IActivitiesSkillExecutor _activitiesExecutor;
    private readonly IDrawingReferenceSkillExecutor _drawingExecutor;
    private readonly ICalendarEventSkillExecutor _calendarExecutor;
    private readonly IVaultSearchSkillExecutor _vaultSearchExecutor;
    private readonly IObsidianCaptureService _obsidianCaptureService;

    public CallbackQueryHandler(
        ILogger<CallbackQueryHandler> logger,
        IConfiguration config,
        ConversationStateManager stateManager,
        IMediaDownloadService mediaDownloadService,
        IAudioTranscriptionService transcriptionService,
        IImageAnalysisService imageAnalysisService,
        ISkillRouter skillRouter,
        IDateQueryTranslator dateTranslator,
        ISummarySkillExecutor summaryExecutor,
        IMotivationSkillExecutor motivationExecutor,
        IActivitiesSkillExecutor activitiesExecutor,
        IDrawingReferenceSkillExecutor drawingExecutor,
        ICalendarEventSkillExecutor calendarExecutor,
        IVaultSearchSkillExecutor vaultSearchExecutor,
        IObsidianCaptureService obsidianCaptureService)
    {
        _logger = logger;
        _stateManager = stateManager;
        _mediaDownloadService = mediaDownloadService;
        _transcriptionService = transcriptionService;
        _imageAnalysisService = imageAnalysisService;
        _skillRouter = skillRouter;
        _dateTranslator = dateTranslator;
        _summaryExecutor = summaryExecutor;
        _motivationExecutor = motivationExecutor;
        _activitiesExecutor = activitiesExecutor;
        _drawingExecutor = drawingExecutor;
        _calendarExecutor = calendarExecutor;
        _vaultSearchExecutor = vaultSearchExecutor;
        _obsidianCaptureService = obsidianCaptureService;

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

        if (data.StartsWith("obsidian:", StringComparison.Ordinal))
        {
            await HandleObsidianCallbackAsync(bot, callbackQuery, chatId.Value, data, ct);
            return;
        }

        if (data.StartsWith("intake:", StringComparison.Ordinal))
        {
            await HandleIncomingChoiceCallbackAsync(bot, callbackQuery, chatId.Value, data, ct);
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
                    var result = await _motivationExecutor.ExecuteAsync(userQuery: null, ct);
                    if (string.IsNullOrWhiteSpace(result)) result = "No motivation available.";
                    return (TruncateForTelegram(result), KeyboardFactory.BuildMotivationRefreshKeyboard());
                }, ct);
                break;

            case "activities":
                await SendProcessingAndExecuteAsync(bot, chatId, "Generating activities...", async () =>
                {
                    var result = await _activitiesExecutor.ExecuteAsync(ct);
                    if (string.IsNullOrWhiteSpace(result)) result = "No activities available.";
                    return (TruncateForTelegram(result), KeyboardFactory.BuildActivitiesRefreshKeyboard());
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
            var result = await _motivationExecutor.ExecuteAsync(userQuery: null, ct);
            if (string.IsNullOrWhiteSpace(result)) result = "No motivation available.";
            return (TruncateForTelegram(result), KeyboardFactory.BuildMotivationRefreshKeyboard());
        }, ct);
    }

    private async Task HandleActivitiesCallbackAsync(ITelegramBotClient bot, CallbackQuery callbackQuery, long chatId, string data, CancellationToken ct)
    {
        await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

        await SendProcessingAndExecuteAsync(bot, chatId, "Generating activities...", async () =>
        {
            var result = await _activitiesExecutor.ExecuteAsync(ct);
            if (string.IsNullOrWhiteSpace(result)) result = "No activities available.";
            return (TruncateForTelegram(result), KeyboardFactory.BuildActivitiesRefreshKeyboard());
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
                var result = await _drawingExecutor.ExecuteAsync(topic, ct);
                _stateManager.SetLastDrawingSource(chatId, result.Source);
                await bot.SendTextMessageAsync(chatId, TruncateForTelegram(result.Message),
                    replyMarkup: KeyboardFactory.BuildDrawingResultKeyboard(result.Source),
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
                var result = await _drawingExecutor.ExecuteAsync(lastSubject, ct);
                _stateManager.SetLastDrawingSource(chatId, result.Source);
                await bot.SendTextMessageAsync(chatId, TruncateForTelegram(result.Message),
                    replyMarkup: KeyboardFactory.BuildDrawingResultKeyboard(result.Source),
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate drawing reference");
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
            }
        }
        else if (action == "try_other_source")
        {
            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

            var lastSubject = _stateManager.GetLastDrawingSubject(chatId);
            if (string.IsNullOrWhiteSpace(lastSubject))
            {
                await SendWithKeyboardAsync(bot, chatId, "No previous subject found. Try /drawref <subject>", ct);
                return;
            }

            var lastSource = _stateManager.GetLastDrawingSource(chatId);
            var newSource = lastSource?.Equals("pexels", StringComparison.OrdinalIgnoreCase) == true ? "unsplash" : "pexels";

            await SendWithKeyboardAsync(bot, chatId, $"Searching on {(newSource == "pexels" ? "Pexels" : "Unsplash")}...", ct);

            try
            {
                var result = await _drawingExecutor.ExecuteFromSourceAsync(lastSubject, newSource, ct);
                _stateManager.SetLastDrawingSource(chatId, result.Source);
                await bot.SendTextMessageAsync(chatId, TruncateForTelegram(result.Message),
                    replyMarkup: KeyboardFactory.BuildDrawingResultKeyboard(result.Source),
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate drawing reference from {Source}", newSource);
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

    private async Task HandleIncomingChoiceCallbackAsync(ITelegramBotClient bot, CallbackQuery callbackQuery, long chatId, string data, CancellationToken ct)
    {
        var action = data["intake:".Length..];

        if (action == "cancel")
        {
            _stateManager.ClearPendingIncomingChoice(chatId);
            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

            if (callbackQuery.Message is not null)
            {
                await bot.EditMessageTextAsync(chatId, callbackQuery.Message.MessageId,
                    "Cancelled.",
                    replyMarkup: null,
                    cancellationToken: ct);
            }

            return;
        }

        var pending = action == "skill"
            ? _stateManager.GetAndRemovePendingIncomingChoice(chatId)
            : _stateManager.PeekPendingIncomingChoice(chatId);

        if (pending is null)
        {
            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Session expired. Please try again.", cancellationToken: ct);
            return;
        }

        await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

        if (action == "obsidian")
        {
            try
            {
                var captureRequest = await BuildObsidianCaptureRequestAsync(bot, pending, ct);
                if (!captureRequest.HasContent)
                {
                    await SendWithKeyboardAsync(bot, chatId, "There is nothing to add to Obsidian.", ct);
                    return;
                }

                _stateManager.ClearPendingIncomingChoice(chatId);
                _stateManager.SetPendingObsidianCapture(chatId, new PendingObsidianCapture
                {
                    Request = captureRequest,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                _stateManager.ClearAwaitingObsidianDate(chatId);

                if (callbackQuery.Message is not null)
                {
                    await bot.EditMessageTextAsync(chatId, callbackQuery.Message.MessageId,
                        "Where should I add this in Obsidian?",
                        replyMarkup: KeyboardFactory.BuildObsidianDestinationKeyboard(),
                        cancellationToken: ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prepare pending content for Obsidian");
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
            }

            return;
        }

        if (callbackQuery.Message is not null)
        {
            await bot.EditMessageTextAsync(chatId, callbackQuery.Message.MessageId,
                "Trying to find a matching Butler skill...",
                replyMarkup: null,
                cancellationToken: ct);
        }

        try
        {
            await ExecuteSkillRoutingAsync(bot, chatId, pending, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process pending incoming choice for skill routing");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task ExecuteSkillRoutingAsync(ITelegramBotClient bot, long chatId, PendingIncomingChoice pending, CancellationToken ct)
    {
        var text = await BuildRoutingTextAsync(bot, pending, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            await SendWithKeyboardAsync(bot, chatId, "I couldn't extract usable text from that message.", ct);
            return;
        }

        var captureRequest = await BuildObsidianCaptureRequestAsync(bot, pending, ct);
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

            _logger.LogInformation(
                "Vault enrichment from callback: query={Query}, originalText={Text}, dateRange={Start} to {End}",
                vaultQuery, text, startDate, endDate);
        }

        switch (routingResult.Skill)
        {
            case ButlerSkill.Motivation:
                await HandleMotivationWithEnrichmentAsync(bot, chatId, text, vaultQuery, startDate, endDate, ct);
                break;
            case ButlerSkill.Activities:
                await HandleActivitiesWithEnrichmentAsync(bot, chatId, text, vaultQuery, startDate, endDate, ct);
                break;
            case ButlerSkill.DrawingReference:
                if (!DrawingReferenceSkillExecutor.TryExtractSubject(text, out var extracted))
                {
                    var randomTopic = _drawingExecutor.GetRandomTopic();
                    _stateManager.SetPendingDrawingTopic(chatId, randomTopic);

                    await bot.SendTextMessageAsync(chatId,
                        $"How about drawing: \"{randomTopic}\"?",
                        replyMarkup: KeyboardFactory.BuildDrawingTopicConfirmationKeyboard(),
                        cancellationToken: ct);
                    return;
                }

                await HandleDrawingReferenceAsync(bot, chatId, extracted!, ct);
                break;
            case ButlerSkill.CalendarEvent:
                var eventText = CalendarEventSkillExecutor.TryExtractEventText(text) ?? text;
                await HandleAddEventAsync(bot, chatId, eventText, ct);
                break;
            case ButlerSkill.VaultSearch:
                if (startDate.HasValue && endDate.HasValue)
                {
                    await HandleSummaryWithEnrichmentAsync(bot, chatId, weekly: false, vaultQuery, startDate, endDate, ct);
                }
                else
                {
                    await HandleVaultSearchAsync(bot, chatId, routingResult.VaultSearchQuery ?? text, ct);
                }

                break;
            case ButlerSkill.AddToObsidian:
                await PromptObsidianDestinationAsync(bot, chatId, captureRequest, ct);
                break;
            case ButlerSkill.Unknown:
                await PromptToAddToObsidianAsync(bot, chatId, captureRequest, "I didn't match that to a Butler skill. Add it to Obsidian?", ct);
                break;
            case ButlerSkill.DailySummary:
                await HandleSummaryWithEnrichmentAsync(bot, chatId, weekly: false, vaultQuery, startDate, endDate, ct);
                break;
            case ButlerSkill.WeeklySummary:
                await HandleSummaryWithEnrichmentAsync(bot, chatId, weekly: true, vaultQuery, startDate, endDate, ct);
                break;
            default:
                await PromptToAddToObsidianAsync(bot, chatId, captureRequest, "I didn't match that to a Butler skill. Add it to Obsidian?", ct);
                break;
        }
    }

    private async Task<string?> BuildRoutingTextAsync(ITelegramBotClient bot, PendingIncomingChoice pending, CancellationToken ct)
    {
        return pending.Kind switch
        {
            PendingIncomingKind.Text => pending.RoutingText,
            PendingIncomingKind.Voice => await TranscribeVoiceAsync(bot, pending, ct),
            PendingIncomingKind.Photo => await AnalyzePhotoForRoutingAsync(bot, pending, ct),
            _ => pending.RoutingText
        };
    }

    private async Task<ObsidianCaptureRequest> BuildObsidianCaptureRequestAsync(ITelegramBotClient bot, PendingIncomingChoice pending, CancellationToken ct)
    {
        if (pending.CaptureRequest is not null)
        {
            return pending.CaptureRequest;
        }

        return pending.Kind switch
        {
            PendingIncomingKind.Text => new ObsidianCaptureRequest
            {
                TextContent = pending.RoutingText
            },
            PendingIncomingKind.Voice => new ObsidianCaptureRequest
            {
                TextContent = await TranscribeVoiceAsync(bot, pending, ct)
            },
            PendingIncomingKind.Photo => await BuildPhotoCaptureRequestAsync(bot, pending, ct),
            _ => new ObsidianCaptureRequest()
        };
    }

    private async Task<string?> TranscribeVoiceAsync(ITelegramBotClient bot, PendingIncomingChoice pending, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pending.TelegramFileId))
        {
            return pending.RoutingText;
        }

        var audioData = await _mediaDownloadService.DownloadFileAsync(bot, pending.TelegramFileId, ct);
        var result = await _transcriptionService.TranscribeAsync(audioData, $"voice_{pending.TelegramFileId}.ogg", ct);
        return string.IsNullOrWhiteSpace(result.Text) ? null : result.Text.Trim();
    }

    private async Task<string?> AnalyzePhotoForRoutingAsync(ITelegramBotClient bot, PendingIncomingChoice pending, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pending.TelegramFileId))
        {
            return pending.CaptionOrText;
        }

        var imageData = await _mediaDownloadService.DownloadFileAsync(bot, pending.TelegramFileId, ct);
        var result = await _imageAnalysisService.AnalyzeAsync(imageData, pending.CaptionOrText, ct);
        var description = result.Description;
        if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(pending.CaptionOrText))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(pending.CaptionOrText) ? description : pending.CaptionOrText;
    }

    private async Task<ObsidianCaptureRequest> BuildPhotoCaptureRequestAsync(ITelegramBotClient bot, PendingIncomingChoice pending, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pending.TelegramFileId))
        {
            return new ObsidianCaptureRequest
            {
                TextContent = CleanObsidianText(pending.CaptionOrText)
            };
        }

        var imageData = await _mediaDownloadService.DownloadFileAsync(bot, pending.TelegramFileId, ct);
        return new ObsidianCaptureRequest
        {
            TextContent = CleanObsidianText(pending.CaptionOrText),
            MediaBytes = imageData,
            MediaFileExtension = pending.MediaFileExtension
        };
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

    private async Task HandleSummaryWithEnrichmentAsync(
        ITelegramBotClient bot,
        long chatId,
        bool weekly,
        string? vaultQuery,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, weekly ? "Generating weekly summary..." : "Generating summary...", ct);

        try
        {
            var taskName = weekly ? "on-demand-weekly" : "on-demand-daily";
            var summary = await _summaryExecutor.ExecuteAsync(weekly, taskName, vaultQuery, startDate, endDate, ct);
            if (string.IsNullOrWhiteSpace(summary)) summary = "No summary available.";

            await SendWithMarkdownFallbackAsync(bot, chatId, TruncateForTelegram(summary),
                KeyboardFactory.BuildSummaryRefreshKeyboard(weekly), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate summary from callback");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task HandleMotivationWithEnrichmentAsync(
        ITelegramBotClient bot,
        long chatId,
        string userQuery,
        string? vaultQuery,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, "Generating motivation...", ct);

        try
        {
            var result = await _motivationExecutor.ExecuteAsync(userQuery, vaultQuery, startDate, endDate, ct);
            if (string.IsNullOrWhiteSpace(result)) result = "No motivation available.";

            await SendWithMarkdownFallbackAsync(bot, chatId, TruncateForTelegram(result),
                KeyboardFactory.BuildMotivationRefreshKeyboard(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate motivation from callback");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task HandleActivitiesWithEnrichmentAsync(
        ITelegramBotClient bot,
        long chatId,
        string userQuery,
        string? vaultQuery,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, "Generating activities...", ct);

        try
        {
            var result = await _activitiesExecutor.ExecuteAsync(userQuery, vaultQuery, startDate, endDate, ct);
            if (string.IsNullOrWhiteSpace(result)) result = "No activities available.";

            await SendWithMarkdownFallbackAsync(bot, chatId, TruncateForTelegram(result),
                KeyboardFactory.BuildActivitiesRefreshKeyboard(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate activities from callback");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task HandleDrawingReferenceAsync(ITelegramBotClient bot, long chatId, string subject, CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, "Finding a drawing reference...", ct);

        try
        {
            _stateManager.SetLastDrawingSubject(chatId, subject);
            var result = await _drawingExecutor.ExecuteAsync(subject, ct);
            _stateManager.SetLastDrawingSource(chatId, result.Source);

            await bot.SendTextMessageAsync(chatId, TruncateForTelegram(result.Message),
                replyMarkup: KeyboardFactory.BuildDrawingResultKeyboard(result.Source),
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate drawing reference from callback");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
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
            _logger.LogWarning(ex, "Failed to parse calendar event from callback");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task HandleVaultSearchAsync(ITelegramBotClient bot, long chatId, string query, CancellationToken ct)
    {
        await SendWithKeyboardAsync(bot, chatId, "Searching vault...", ct);

        try
        {
            var result = await _vaultSearchExecutor.ExecuteAsync(query, ct);
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(result), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search vault from callback");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task PromptObsidianDestinationAsync(
        ITelegramBotClient bot,
        long chatId,
        ObsidianCaptureRequest request,
        CancellationToken ct)
    {
        if (!request.HasContent)
        {
            await SendWithKeyboardAsync(bot, chatId, "There is nothing to add to Obsidian.", ct);
            return;
        }

        _stateManager.SetPendingObsidianCapture(chatId, new PendingObsidianCapture
        {
            Request = request,
            CreatedAt = DateTimeOffset.UtcNow
        });
        _stateManager.ClearAwaitingObsidianDate(chatId);

        await bot.SendTextMessageAsync(chatId,
            "Where should I add this in Obsidian?",
            replyMarkup: KeyboardFactory.BuildObsidianDestinationKeyboard(),
            cancellationToken: ct);
    }

    private async Task PromptToAddToObsidianAsync(
        ITelegramBotClient bot,
        long chatId,
        ObsidianCaptureRequest request,
        string prompt,
        CancellationToken ct)
    {
        if (!request.HasContent)
        {
            await SendWithKeyboardAsync(bot, chatId, "There is nothing to add to Obsidian.", ct);
            return;
        }

        _stateManager.SetPendingObsidianCapture(chatId, new PendingObsidianCapture
        {
            Request = request,
            CreatedAt = DateTimeOffset.UtcNow
        });
        _stateManager.ClearAwaitingObsidianDate(chatId);

        await bot.SendTextMessageAsync(chatId,
            prompt,
            replyMarkup: KeyboardFactory.BuildObsidianConfirmKeyboard(),
            cancellationToken: ct);
    }

    private async Task HandleObsidianCallbackAsync(ITelegramBotClient bot, CallbackQuery callbackQuery, long chatId, string data, CancellationToken ct)
    {
        var action = data["obsidian:".Length..];

        if (action == "confirm_add")
        {
            var pending = _stateManager.PeekPendingObsidianCapture(chatId);
            if (pending is null)
            {
                await bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Session expired. Please try again.", cancellationToken: ct);
                return;
            }

            _stateManager.ClearAwaitingObsidianDate(chatId);
            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

            if (callbackQuery.Message is not null)
            {
                await bot.EditMessageTextAsync(chatId, callbackQuery.Message.MessageId,
                    "Where should I add this in Obsidian?",
                    replyMarkup: KeyboardFactory.BuildObsidianDestinationKeyboard(),
                    cancellationToken: ct);
            }

            return;
        }

        if (action == "other_date")
        {
            var pending = _stateManager.PeekPendingObsidianCapture(chatId);
            if (pending is null)
            {
                await bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Session expired. Please try again.", cancellationToken: ct);
                return;
            }

            _stateManager.SetAwaitingObsidianDate(chatId, true);
            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

            if (callbackQuery.Message is not null)
            {
                await bot.EditMessageTextAsync(chatId, callbackQuery.Message.MessageId,
                    "Send the date as YYYY-MM-DD.",
                    replyMarkup: null,
                    cancellationToken: ct);
            }

            return;
        }

        if (action == "cancel")
        {
            _stateManager.ClearPendingObsidianCapture(chatId);
            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

            if (callbackQuery.Message is not null)
            {
                await bot.EditMessageTextAsync(chatId, callbackQuery.Message.MessageId,
                    "Obsidian save cancelled.",
                    replyMarkup: null,
                    cancellationToken: ct);
            }

            return;
        }

        var pendingCapture = _stateManager.GetAndRemovePendingObsidianCapture(chatId);
        if (pendingCapture is null)
        {
            await bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Session expired. Please try again.", cancellationToken: ct);
            return;
        }

        await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

        if (callbackQuery.Message is not null)
        {
            var statusText = action == "today"
                ? "Adding to today's daily note..."
                : "Adding to inbox note...";

            await bot.EditMessageTextAsync(chatId, callbackQuery.Message.MessageId,
                statusText,
                replyMarkup: null,
                cancellationToken: ct);
        }

        try
        {
            ObsidianCaptureResult result = action switch
            {
                "today" => await _obsidianCaptureService.AppendToTodayDailyNoteAsync(pendingCapture.Request, ct),
                "inbox" => await _obsidianCaptureService.AppendToInboxNoteAsync(pendingCapture.Request, ct),
                _ => throw new InvalidOperationException($"Unknown Obsidian action '{action}'.")
            };

            await SendWithKeyboardAsync(bot, chatId, BuildObsidianSavedMessage(result), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save content to Obsidian via callback action {Action}", action);
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
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
            await SendWithMarkdownFallbackAsync(bot, chatId, text, keyboard, ct);
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

    /// <summary>
    /// Sends a message with Markdown parsing, falling back to plain text if parsing fails.
    /// </summary>
    private static async Task SendWithMarkdownFallbackAsync(
        ITelegramBotClient bot,
        long chatId,
        string text,
        global::Telegram.Bot.Types.ReplyMarkups.IReplyMarkup? replyMarkup,
        CancellationToken ct)
    {
        try
        {
            await bot.SendTextMessageAsync(chatId, text, parseMode: ParseMode.Markdown, replyMarkup: replyMarkup, cancellationToken: ct);
        }
        catch (global::Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase))
        {
            // Markdown parsing failed, retry without parsing
            await bot.SendTextMessageAsync(chatId, text, replyMarkup: replyMarkup, cancellationToken: ct);
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

    private static string BuildObsidianSavedMessage(ObsidianCaptureResult result)
    {
        var message = $"Saved to {result.TargetDescription}: {result.NotePath}";
        if (!string.IsNullOrWhiteSpace(result.MediaFileName))
        {
            message += $"\nMedia: {result.MediaFileName}";
        }

        return message;
    }
}
