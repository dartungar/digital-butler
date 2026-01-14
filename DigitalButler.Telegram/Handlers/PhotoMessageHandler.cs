using DigitalButler.Common;
using DigitalButler.Context;
using DigitalButler.Skills;
using DigitalButler.Telegram.Skills;
using DigitalButler.Telegram.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace DigitalButler.Telegram.Handlers;

public sealed class PhotoMessageHandler : IPhotoMessageHandler
{
    private readonly ILogger<PhotoMessageHandler> _logger;
    private readonly long _allowedUserId;
    private readonly IMediaDownloadService _mediaDownloadService;
    private readonly IImageAnalysisService _imageAnalysisService;
    private readonly ContextService _contextService;
    private readonly ISkillRouter _skillRouter;
    private readonly ISummarySkillExecutor _summaryExecutor;
    private readonly IMotivationSkillExecutor _motivationExecutor;
    private readonly IActivitiesSkillExecutor _activitiesExecutor;
    private readonly ICalendarEventSkillExecutor _calendarExecutor;

    public PhotoMessageHandler(
        ILogger<PhotoMessageHandler> logger,
        IConfiguration config,
        IMediaDownloadService mediaDownloadService,
        IImageAnalysisService imageAnalysisService,
        ContextService contextService,
        ISkillRouter skillRouter,
        ISummarySkillExecutor summaryExecutor,
        IMotivationSkillExecutor motivationExecutor,
        IActivitiesSkillExecutor activitiesExecutor,
        ICalendarEventSkillExecutor calendarExecutor)
    {
        _logger = logger;
        _mediaDownloadService = mediaDownloadService;
        _imageAnalysisService = imageAnalysisService;
        _contextService = contextService;
        _skillRouter = skillRouter;
        _summaryExecutor = summaryExecutor;
        _motivationExecutor = motivationExecutor;
        _activitiesExecutor = activitiesExecutor;
        _calendarExecutor = calendarExecutor;

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

        await SendWithKeyboardAsync(bot, chatId, "Analyzing image...", ct);

        try
        {
            // Get largest photo
            var largestPhoto = message.Photo.OrderByDescending(p => p.FileSize).First();

            // Download image
            var imageData = await _mediaDownloadService.DownloadFileAsync(bot, largestPhoto.FileId, ct);

            // Analyze image
            var result = await _imageAnalysisService.AnalyzeAsync(imageData, caption, ct);
            var description = result.Description;

            if (string.IsNullOrWhiteSpace(description))
            {
                await SendWithKeyboardAsync(bot, chatId, "Could not analyze the image.", ct);
                return;
            }

            // Route through skill router - use caption for routing if available
            var textForRouting = string.IsNullOrWhiteSpace(caption) ? description : caption;
            var route = await _skillRouter.RouteAsync(textForRouting, ct);

            switch (route.Skill)
            {
                case ButlerSkill.CalendarEvent:
                    var eventText = string.IsNullOrWhiteSpace(caption) ? description : caption;
                    await HandleCalendarEventAsync(bot, chatId, eventText, ct);
                    break;

                case ButlerSkill.Motivation:
                    var motivation = await _motivationExecutor.ExecuteAsync(ct);
                    await bot.SendTextMessageAsync(chatId, TruncateForTelegram(motivation ?? "No motivation available."),
                        replyMarkup: KeyboardFactory.BuildMotivationRefreshKeyboard(),
                        cancellationToken: ct);
                    break;

                case ButlerSkill.Activities:
                    var activities = await _activitiesExecutor.ExecuteAsync(ct);
                    await bot.SendTextMessageAsync(chatId, TruncateForTelegram(activities ?? "No activities available."),
                        replyMarkup: KeyboardFactory.BuildActivitiesRefreshKeyboard(),
                        cancellationToken: ct);
                    break;

                case ButlerSkill.Summary:
                    var taskName = route.PreferWeeklySummary ? "on-demand-weekly" : "on-demand-daily";
                    var summary = await _summaryExecutor.ExecuteAsync(route.PreferWeeklySummary, taskName, ct);
                    await bot.SendTextMessageAsync(chatId, TruncateForTelegram(summary ?? "No summary available."),
                        replyMarkup: KeyboardFactory.BuildSummaryRefreshKeyboard(route.PreferWeeklySummary),
                        cancellationToken: ct);
                    break;

                case ButlerSkill.DrawingReference:
                default:
                    // Save image analysis to context
                    var title = string.IsNullOrWhiteSpace(caption) ? "Image" : caption;
                    await _contextService.AddPersonalAsync(
                        body: description,
                        title: title,
                        mediaMetadata: string.IsNullOrWhiteSpace(caption) ? null : caption,
                        mediaType: "image",
                        ct: ct);

                    var responseText = string.IsNullOrWhiteSpace(caption)
                        ? $"Image analyzed and saved:\n\n{description}"
                        : $"Image analyzed and saved:\n\nCaption: {caption}\n\nAnalysis: {description}";

                    await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(responseText), ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process photo message");
            await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), ct);
        }
    }

    private async Task HandleCalendarEventAsync(ITelegramBotClient bot, long chatId, string eventText, CancellationToken ct)
    {
        if (!_calendarExecutor.IsConfigured)
        {
            await SendWithKeyboardAsync(bot, chatId,
                "Google Calendar is not configured.",
                ct);
            return;
        }

        var result = await _calendarExecutor.ParseEventAsync(eventText, ct);
        if (result is null)
        {
            await SendWithKeyboardAsync(bot, chatId,
                "I couldn't understand that event.",
                ct);
            return;
        }

        // For photo messages, create the event directly without confirmation
        var createResult = await _calendarExecutor.CreateEventAsync(result.Parsed, ct);
        if (createResult.Success)
        {
            await SendWithKeyboardAsync(bot, chatId,
                $"Event created: {result.Parsed.Title}\n{createResult.HtmlLink}",
                ct);
        }
        else
        {
            await SendWithKeyboardAsync(bot, chatId,
                $"Failed to create event: {createResult.Error}",
                ct);
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
