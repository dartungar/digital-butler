using DigitalButler.Context;
using DigitalButler.Common;
using DigitalButler.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Polling = Telegram.Bot.Polling;
using Telegram.Bot.Types;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DigitalButler.Telegram;

public class BotService : Microsoft.Extensions.Hosting.IHostedService, IDisposable
{
    private readonly ILogger<BotService> _logger;
    private readonly IServiceProvider _services;
    private readonly string _token;
    private readonly long _allowedUserId;
    private readonly TimeSpan _startupPingTimeout;
    private readonly bool _forceIpv4;
    private TelegramBotClient? _bot;
    private CancellationTokenSource? _cts;
    private Task? _runLoop;

    // Simple in-memory conversation state for clarifying the drawing subject.
    // Key: chat id. Value: awaiting subject.
    private readonly ConcurrentDictionary<long, bool> _awaitingDrawingSubject = new();

    // Pending random topic confirmations.
    // Key: chat id. Value: suggested topic.
    private readonly ConcurrentDictionary<long, string> _pendingTopicConfirmation = new();

    // Pending calendar event confirmations.
    // Key: chat id. Value: pending event details.
    private readonly ConcurrentDictionary<long, PendingCalendarEvent> _pendingEventConfirmation = new();

    private readonly record struct PendingCalendarEvent(ParsedCalendarEvent Parsed, DateTimeOffset CreatedAt);

    public BotService(ILogger<BotService> logger, IServiceProvider services, IConfiguration config)
    {
        _logger = logger;
        _services = services;
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
        
        var allowedUserIdStr = config["TELEGRAM_ALLOWED_USER_ID"];
        _allowedUserId = string.IsNullOrWhiteSpace(allowedUserIdStr) 
            ? throw new InvalidOperationException("TELEGRAM_ALLOWED_USER_ID not configured") 
            : long.Parse(allowedUserIdStr);
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
                // Most commonly a timeout from pingCts.CancelAfter(...).
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

                // Exponential backoff up to 60s.
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

                // Exponential backoff up to 60s.
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
        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is not null)
        {
            await HandleCallbackQueryAsync(bot, update.CallbackQuery, ct);
            return;
        }

        if (update.Type != UpdateType.Message || update.Message?.Text is null)
            return;

        var chatId = update.Message.Chat.Id;
        var userId = update.Message.From?.Id;
        var text = update.Message.Text.Trim();

        // Authorization check: only allow configured user
        if (userId != _allowedUserId)
        {
            _logger.LogWarning("Unauthorized access attempt from user {UserId}", userId);
            await SendWithKeyboardAsync(bot, chatId, "Unauthorized.", cancellationToken: ct);
            return;
        }

        using var scope = _services.CreateScope();
        var contextService = scope.ServiceProvider.GetRequiredService<ContextService>();
        var instructionService = scope.ServiceProvider.GetRequiredService<InstructionService>();
        var skillInstructionService = scope.ServiceProvider.GetRequiredService<SkillInstructionService>();
        var summarizer = scope.ServiceProvider.GetRequiredService<ISummarizationService>();
        var skillRouter = scope.ServiceProvider.GetRequiredService<ISkillRouter>();
        var aiContext = scope.ServiceProvider.GetRequiredService<IAiContextAugmenter>();
        var tzService = scope.ServiceProvider.GetRequiredService<TimeZoneService>();
        var drawingRef = scope.ServiceProvider.GetRequiredService<IDrawingReferenceService>();
        var subjectTranslator = scope.ServiceProvider.GetRequiredService<ISubjectTranslator>();

        // If we previously asked for a drawing subject, treat the next non-command message as the subject.
        if (_awaitingDrawingSubject.TryGetValue(chatId, out var awaiting) && awaiting && !text.StartsWith("/", StringComparison.OrdinalIgnoreCase))
        {
            _awaitingDrawingSubject.TryRemove(chatId, out _);
            await SendWithKeyboardAsync(bot, chatId, "Finding a drawing reference...", cancellationToken: ct);
            try
            {
                var reply = await ExecuteDrawingReferenceAsync(drawingRef, subjectTranslator, text, ct);
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(reply), cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate drawing reference");
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), cancellationToken: ct);
            }
            return;
        }

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await SendWithKeyboardAsync(bot, chatId,
                "Commands:\n" +
                "/daily - today + timeless\n" +
                "/weekly - this week + timeless\n" +
                "/motivation - motivational message\n" +
                "/activities - activity suggestions\n" +
                "/drawref <subject> - drawing reference image\n" +
                "/addevent <text> - add a calendar event\n" +
                "/add <text> - add personal context\n" +
                "/sync - run all ingests now",
                cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/drawref", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/drawingref", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/drawing_reference", StringComparison.OrdinalIgnoreCase))
        {
            var subject = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(subject))
            {
                // No subject provided - suggest a random topic
                var topicService = scope.ServiceProvider.GetRequiredService<IRandomDrawingTopicService>();
                var randomTopic = topicService.GetRandomTopic();
                _pendingTopicConfirmation[chatId] = randomTopic;

                await bot.SendTextMessageAsync(
                    chatId,
                    $"How about drawing: \"{randomTopic}\"?",
                    replyMarkup: BuildDrawingTopicConfirmationKeyboard(),
                    cancellationToken: ct);
                return;
            }

            await SendWithKeyboardAsync(bot, chatId, "Finding a drawing reference...", cancellationToken: ct);
            try
            {
                var reply = await ExecuteDrawingReferenceAsync(drawingRef, subjectTranslator, subject, ct);
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(reply), cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate /drawref");
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), cancellationToken: ct);
            }
            return;
        }

        if (text.StartsWith("/addevent", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/newevent", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/event ", StringComparison.OrdinalIgnoreCase))
        {
            var eventText = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(eventText))
            {
                await SendWithKeyboardAsync(bot, chatId,
                    "Usage: /addevent Meeting with John tomorrow at 3pm",
                    cancellationToken: ct);
                return;
            }

            await HandleAddEventAsync(bot, chatId, eventText, scope, ct);
            return;
        }

        if (text.StartsWith("/add", StringComparison.OrdinalIgnoreCase))
        {
            var content = text[4..].Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                await SendWithKeyboardAsync(bot, chatId, "Usage: /add your note", cancellationToken: ct);
                return;
            }

            await contextService.AddPersonalAsync(content, ct: ct);
            await SendWithKeyboardAsync(bot, chatId, "Saved", cancellationToken: ct);
            return;
        }

        // Backwards compat: /summary behaves like /daily.
        if (text.StartsWith("/summary", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/daily", StringComparison.OrdinalIgnoreCase))
        {
            await SendWithKeyboardAsync(bot, chatId, "Generating daily summary...", cancellationToken: ct);
            try
            {
                var summary = await ExecuteSummaryAsync(contextService, instructionService, skillInstructionService, summarizer, aiContext, tzService, weekly: false, taskName: "on-demand-daily", ct);
                if (string.IsNullOrWhiteSpace(summary)) summary = "No summary available.";
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(summary), cancellationToken: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host is shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate /daily");
                var msg = BuildUserFacingError(ex);
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(msg), cancellationToken: ct);
            }
            return;
        }

        if (text.StartsWith("/weekly", StringComparison.OrdinalIgnoreCase))
        {
            await SendWithKeyboardAsync(bot, chatId, "Generating weekly summary...", cancellationToken: ct);
            try
            {
                var summary = await ExecuteSummaryAsync(contextService, instructionService, skillInstructionService, summarizer, aiContext, tzService, weekly: true, taskName: "on-demand-weekly", ct);
                if (string.IsNullOrWhiteSpace(summary)) summary = "No summary available.";
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(summary), cancellationToken: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host is shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate /weekly");
                var msg = BuildUserFacingError(ex);
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(msg), cancellationToken: ct);
            }

            return;
        }

        if (text.StartsWith("/sync", StringComparison.OrdinalIgnoreCase))
        {
            var runner = scope.ServiceProvider.GetRequiredService<IManualSyncRunner>();
            await SendWithKeyboardAsync(bot, chatId, "Running ingests...", cancellationToken: ct);
            try
            {
                var result = await runner.RunAllAsync(ct);
                var summary = $"Sync finished in {(result.FinishedAt - result.StartedAt).TotalSeconds:0.#}s. " +
                              $"Updaters: {result.UpdatersRun}, Failures: {result.Failures}.";
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(summary + "\n" + string.Join("\n", result.Messages)), cancellationToken: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host is shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to run /sync");
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), cancellationToken: ct);
            }
            return;
        }

        if (text.StartsWith("/motivation", StringComparison.OrdinalIgnoreCase))
        {
            await SendWithKeyboardAsync(bot, chatId, "Generating motivation...", cancellationToken: ct);
            try
            {
                var result = await ExecuteMotivationAsync(contextService, instructionService, skillInstructionService, summarizer, aiContext, tzService, ct);
                if (string.IsNullOrWhiteSpace(result)) result = "No motivation available.";
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(result), cancellationToken: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host is shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate /motivation");
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), cancellationToken: ct);
            }
            return;
        }

        if (text.StartsWith("/activities", StringComparison.OrdinalIgnoreCase))
        {
            await SendWithKeyboardAsync(bot, chatId, "Generating activities...", cancellationToken: ct);
            try
            {
                var result = await ExecuteActivitiesAsync(contextService, instructionService, skillInstructionService, summarizer, aiContext, tzService, ct);
                if (string.IsNullOrWhiteSpace(result)) result = "No activities available.";
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(result), cancellationToken: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host is shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate /activities");
                await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), cancellationToken: ct);
            }
            return;
        }

        // Plain text: route to a skill using AI.
        if (!text.StartsWith("/", StringComparison.OrdinalIgnoreCase))
        {
            var route = await skillRouter.RouteAsync(text, ct);
            switch (route.Skill)
            {
                case ButlerSkill.Motivation:
                    await SendWithKeyboardAsync(bot, chatId, "Generating motivation...", cancellationToken: ct);
                    var motivation = await ExecuteMotivationAsync(contextService, instructionService, skillInstructionService, summarizer, aiContext, tzService, ct);
                    if (string.IsNullOrWhiteSpace(motivation)) motivation = "No motivation available.";
                    await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(motivation), cancellationToken: ct);
                    return;
                case ButlerSkill.Activities:
                    await SendWithKeyboardAsync(bot, chatId, "Generating activities...", cancellationToken: ct);
                    var activities = await ExecuteActivitiesAsync(contextService, instructionService, skillInstructionService, summarizer, aiContext, tzService, ct);
                    if (string.IsNullOrWhiteSpace(activities)) activities = "No activities available.";
                    await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(activities), cancellationToken: ct);
                    return;
                case ButlerSkill.DrawingReference:
                    if (!TryExtractDrawingSubject(text, out var extracted))
                    {
                        // No subject extracted - suggest a random topic
                        var topicService = scope.ServiceProvider.GetRequiredService<IRandomDrawingTopicService>();
                        var randomTopic = topicService.GetRandomTopic();
                        _pendingTopicConfirmation[chatId] = randomTopic;

                        await bot.SendTextMessageAsync(
                            chatId,
                            $"How about drawing: \"{randomTopic}\"?",
                            replyMarkup: BuildDrawingTopicConfirmationKeyboard(),
                            cancellationToken: ct);
                        return;
                    }

                    await SendWithKeyboardAsync(bot, chatId, "Finding a drawing reference...", cancellationToken: ct);
                    try
                    {
                        var reply = await ExecuteDrawingReferenceAsync(drawingRef, subjectTranslator, extracted!, ct);
                        await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(reply), cancellationToken: ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to generate drawing reference");
                        await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(BuildUserFacingError(ex)), cancellationToken: ct);
                    }
                    return;
                case ButlerSkill.CalendarEvent:
                    // Extract event text - try to get the relevant part after common prefixes
                    var eventText = TryExtractEventText(text) ?? text;
                    await HandleAddEventAsync(bot, chatId, eventText, scope, ct);
                    return;
                case ButlerSkill.Summary:
                default:
                    await SendWithKeyboardAsync(bot, chatId, route.PreferWeeklySummary ? "Generating weekly summary..." : "Generating daily summary...", cancellationToken: ct);
                    var summary = await ExecuteSummaryAsync(contextService, instructionService, skillInstructionService, summarizer, aiContext, tzService, weekly: route.PreferWeeklySummary, taskName: route.PreferWeeklySummary ? "on-demand-weekly" : "on-demand-daily", ct);
                    if (string.IsNullOrWhiteSpace(summary)) summary = "No summary available.";
                    await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(summary), cancellationToken: ct);
                    return;
            }
        }

        await SendWithKeyboardAsync(bot, chatId, "Unknown command. Use /daily, /weekly, /motivation, /activities, /addevent, /add <text>", cancellationToken: ct);
    }

    private async Task HandleCallbackQueryAsync(ITelegramBotClient bot, CallbackQuery callbackQuery, CancellationToken ct)
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

        // Handle drawing topic confirmation callbacks
        if (data.StartsWith("drawref:", StringComparison.Ordinal))
        {
            var action = data["drawref:".Length..];

            if (action == "confirm")
            {
                if (_pendingTopicConfirmation.TryRemove(chatId.Value, out var topic))
                {
                    await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

                    // Edit the original message to remove the inline keyboard
                    if (callbackQuery.Message is not null)
                    {
                        await bot.EditMessageTextAsync(
                            chatId.Value,
                            callbackQuery.Message.MessageId,
                            $"Drawing topic: {topic}",
                            replyMarkup: null,
                            cancellationToken: ct);
                    }

                    await SendWithKeyboardAsync(bot, chatId.Value, "Finding a drawing reference...", cancellationToken: ct);

                    using var scope = _services.CreateScope();
                    var drawingRef = scope.ServiceProvider.GetRequiredService<IDrawingReferenceService>();
                    var subjectTranslator = scope.ServiceProvider.GetRequiredService<ISubjectTranslator>();

                    try
                    {
                        var reply = await ExecuteDrawingReferenceAsync(drawingRef, subjectTranslator, topic, ct);
                        await SendWithKeyboardAsync(bot, chatId.Value, TruncateForTelegram(reply), cancellationToken: ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to generate drawing reference after confirmation");
                        await SendWithKeyboardAsync(bot, chatId.Value, TruncateForTelegram(BuildUserFacingError(ex)), cancellationToken: ct);
                    }
                }
                else
                {
                    await bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Session expired. Please try again.", cancellationToken: ct);
                }
            }
            else if (action == "another")
            {
                await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

                using var scope = _services.CreateScope();
                var topicService = scope.ServiceProvider.GetRequiredService<IRandomDrawingTopicService>();

                var newTopic = topicService.GetRandomTopic();
                _pendingTopicConfirmation[chatId.Value] = newTopic;

                // Edit the original message with the new topic suggestion
                if (callbackQuery.Message is not null)
                {
                    await bot.EditMessageTextAsync(
                        chatId.Value,
                        callbackQuery.Message.MessageId,
                        $"How about drawing: \"{newTopic}\"?",
                        replyMarkup: BuildDrawingTopicConfirmationKeyboard(),
                        cancellationToken: ct);
                }
            }

            return;
        }

        // Handle calendar event confirmation callbacks
        if (data.StartsWith("calevent:", StringComparison.Ordinal))
        {
            var action = data["calevent:".Length..];

            if (action == "confirm")
            {
                if (_pendingEventConfirmation.TryRemove(chatId.Value, out var pending))
                {
                    await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

                    // Edit the original message to show we're creating the event
                    if (callbackQuery.Message is not null)
                    {
                        await bot.EditMessageTextAsync(
                            chatId.Value,
                            callbackQuery.Message.MessageId,
                            $"Creating event: {pending.Parsed.Title}...",
                            replyMarkup: null,
                            cancellationToken: ct);
                    }

                    using var scope = _services.CreateScope();
                    var calendarService = scope.ServiceProvider.GetRequiredService<IGoogleCalendarEventService>();

                    var result = await calendarService.CreateEventAsync(pending.Parsed, ct);

                    if (result.Success)
                    {
                        await SendWithKeyboardAsync(bot, chatId.Value,
                            $"Event created: {pending.Parsed.Title}\n{result.HtmlLink}",
                            cancellationToken: ct);
                    }
                    else
                    {
                        await SendWithKeyboardAsync(bot, chatId.Value,
                            $"Failed to create event: {result.Error}",
                            cancellationToken: ct);
                    }
                }
                else
                {
                    await bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Session expired. Please try again.", cancellationToken: ct);
                }
            }
            else if (action == "reject")
            {
                _pendingEventConfirmation.TryRemove(chatId.Value, out _);
                await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

                if (callbackQuery.Message is not null)
                {
                    await bot.EditMessageTextAsync(
                        chatId.Value,
                        callbackQuery.Message.MessageId,
                        "Event creation cancelled.",
                        replyMarkup: null,
                        cancellationToken: ct);
                }
            }

            return;
        }

        // Unknown callback
        await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);
    }

    private static InlineKeyboardMarkup BuildDrawingTopicConfirmationKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Yes, let's go!", "drawref:confirm"),
                InlineKeyboardButton.WithCallbackData("Suggest another", "drawref:another")
            }
        });
    }

    private static ReplyKeyboardMarkup BuildKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("/daily"), new KeyboardButton("/weekly") },
            new[] { new KeyboardButton("/motivation"), new KeyboardButton("/activities") },
            new[] { new KeyboardButton("/add ") }
        })
        {
            ResizeKeyboard = true
        };
    }

    private static async Task<SkillInstruction?> GetSkillConfigAsync(SkillInstructionService svc, ButlerSkill skill, CancellationToken ct)
    {
        var dict = await svc.GetFullBySkillsAsync(new[] { skill }, ct);
        return dict.TryGetValue(skill, out var v) ? v : null;
    }

    private static async Task<string> ExecuteSummaryAsync(
        ContextService contextService,
        InstructionService instructionService,
        SkillInstructionService skillInstructionService,
        ISummarizationService summarizer,
        IAiContextAugmenter aiContext,
        TimeZoneService tzService,
        bool weekly,
        string taskName,
        CancellationToken ct)
    {
        var tz = await tzService.GetTimeZoneInfoAsync(ct);
        var items = weekly
            ? await GetWeeklyItemsAsync(contextService, tz, ct)
            : await GetDailyItemsAsync(contextService, tz, ct);

        var cfg = await GetSkillConfigAsync(skillInstructionService, ButlerSkill.Summary, ct);
        var allowedMask = SkillContextDefaults.ResolveSourcesMask(ButlerSkill.Summary, cfg?.ContextSourcesMask ?? -1);
        items = items.Where(x => ContextSourceMask.Contains(allowedMask, x.Source)).ToList();

        if (cfg?.EnableAiContext == true)
        {
            var snippet = await aiContext.GenerateAsync(ButlerSkill.Summary, items, taskName, ct);
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                items.Add(new ContextItem
                {
                    Source = ContextSource.Personal,
                    Title = "AI self-thought",
                    Body = snippet.Trim(),
                    IsTimeless = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        var sources = items.Select(x => x.Source).Distinct().ToArray();
        var instructionsBySource = await instructionService.GetBySourcesAsync(sources, ct);
        var skillInstructions = cfg?.Content;
        var period = weekly ? "weekly" : "daily";
        return await summarizer.SummarizeAsync(items, instructionsBySource, taskName, BuildSummarySkillPrompt(period, skillInstructions), ct);
    }

    private static async Task<string> ExecuteMotivationAsync(
        ContextService contextService,
        InstructionService instructionService,
        SkillInstructionService skillInstructionService,
        ISummarizationService summarizer,
        IAiContextAugmenter aiContext,
        TimeZoneService tzService,
        CancellationToken ct)
    {
        var tz = await tzService.GetTimeZoneInfoAsync(ct);
        var items = await contextService.GetRelevantAsync(daysBack: 30, take: 250, ct: ct);

        var cfg = await GetSkillConfigAsync(skillInstructionService, ButlerSkill.Motivation, ct);
        var allowedMask = SkillContextDefaults.ResolveSourcesMask(ButlerSkill.Motivation, cfg?.ContextSourcesMask ?? -1);
        items = items.Where(x => ContextSourceMask.Contains(allowedMask, x.Source)).ToList();

        if (cfg?.EnableAiContext == true)
        {
            var snippet = await aiContext.GenerateAsync(ButlerSkill.Motivation, items, "motivation", ct);
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                items.Add(new ContextItem
                {
                    Source = ContextSource.Personal,
                    Title = "AI self-thought",
                    Body = snippet.Trim(),
                    IsTimeless = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        // Motivation should be driven by Personal context and per-skill instructions only.
        // Do NOT apply per-source instructions (e.g. Google Calendar formatting) to this skill.
        var instructionsBySource = new Dictionary<ContextSource, string>();
        var prompt = BuildMotivationSkillPrompt(cfg?.Content);
        return await summarizer.SummarizeAsync(items, instructionsBySource, "motivation", prompt, ct);
    }

    private static async Task<string> ExecuteActivitiesAsync(
        ContextService contextService,
        InstructionService instructionService,
        SkillInstructionService skillInstructionService,
        ISummarizationService summarizer,
        IAiContextAugmenter aiContext,
        TimeZoneService tzService,
        CancellationToken ct)
    {
        var tz = await tzService.GetTimeZoneInfoAsync(ct);
        var items = await contextService.GetRelevantAsync(daysBack: 14, take: 250, ct: ct);

        var cfg = await GetSkillConfigAsync(skillInstructionService, ButlerSkill.Activities, ct);
        var allowedMask = SkillContextDefaults.ResolveSourcesMask(ButlerSkill.Activities, cfg?.ContextSourcesMask ?? -1);
        items = items.Where(x => ContextSourceMask.Contains(allowedMask, x.Source)).ToList();

        if (cfg?.EnableAiContext == true)
        {
            var snippet = await aiContext.GenerateAsync(ButlerSkill.Activities, items, "activities", ct);
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                items.Add(new ContextItem
                {
                    Source = ContextSource.Personal,
                    Title = "AI self-thought",
                    Body = snippet.Trim(),
                    IsTimeless = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        // Activities should be driven by Personal context and per-skill instructions only.
        // Do NOT apply per-source instructions (e.g. Google Calendar formatting) to this skill.
        var instructionsBySource = new Dictionary<ContextSource, string>();
        var prompt = BuildActivitiesSkillPrompt(cfg?.Content);
        return await summarizer.SummarizeAsync(items, instructionsBySource, "activities", prompt, ct);
    }

    private static string BuildSummarySkillPrompt(string period, string? custom)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Skill: summary");
        sb.AppendLine($"Period: {period}");
        sb.AppendLine("Output a concise agenda with actionable highlights.");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            sb.AppendLine();
            sb.AppendLine(custom.Trim());
        }
        return sb.ToString();
    }

    private static string BuildMotivationSkillPrompt(string? custom)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Skill: motivation");
        sb.AppendLine("Write a short motivational message grounded ONLY in Personal context items.");
        sb.AppendLine("Ignore calendar/events/emails unless they are part of Personal context.");
        sb.AppendLine("Do not summarize the notes; do not quote them; use them only as inspiration.");
        sb.AppendLine("Do not mention that you are an AI or that you were given 'context items'.");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            sb.AppendLine();
            sb.AppendLine(custom.Trim());
        }
        return sb.ToString();
    }

    private static string? TryExtractEventText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lowered = text.Trim();

        // Extract the event description from common patterns
        static string? After(string input, string needle)
        {
            var idx = input.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            return input[(idx + needle.Length)..].Trim();
        }

        // Try to extract from common patterns
        var tail = After(lowered, "create event ")
                   ?? After(lowered, "schedule ")
                   ?? After(lowered, "add event ")
                   ?? After(lowered, "new event ")
                   ?? After(lowered, "add ");

        return string.IsNullOrWhiteSpace(tail) ? text : tail;
    }

    private static bool TryExtractDrawingSubject(string text, out string? subject)
    {
        subject = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lowered = text.Trim();

        // Heuristic extraction:
        // - "draw <subject>", "drawing <subject>", "sketch <subject>"
        // - "drawing reference for <subject>"
        static string? After(string input, string needle)
        {
            var idx = input.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            return input[(idx + needle.Length)..].Trim();
        }

        var tail = After(lowered, "drawing reference for ")
                   ?? After(lowered, "reference for ")
                   ?? After(lowered, "draw ")
                   ?? After(lowered, "drawing ")
                   ?? After(lowered, "sketch ");

        if (string.IsNullOrWhiteSpace(tail))
        {
            return false;
        }

        // Strip leading filler words.
        var cleaned = tail.Trim().Trim('.', '!', '?', ':', ';', ',', '"', '\'', ')', '(', '[', ']', '{', '}');
        foreach (var stop in new[] { "some ", "a ", "an ", "the ", "my ", "any " })
        {
            if (cleaned.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[stop.Length..].Trim();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.StartsWith("practice", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Reject vague/non-subject words that don't represent a drawable subject.
        var vague = new[]
        {
            "something", "anything", "stuff", "things", "thing",
            "time", "now", "today", "session", "practice",
            "please", "thanks", "help", "me", "it", "this", "that",
            "idk", "dunno", "whatever", "random", "surprise", "shit", "что-нибудь", "что-то"
        };
        if (vague.Any(v => cleaned.Equals(v, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        subject = cleaned;
        return true;
    }

    private static async Task<string> ExecuteDrawingReferenceAsync(IDrawingReferenceService svc, ISubjectTranslator translator, string subject, CancellationToken ct)
    {
        var original = subject.Trim();
        var translated = await translator.TranslateToEnglishAsync(original, ct);
        if (string.IsNullOrWhiteSpace(translated))
        {
            translated = original;
        }

        var result = await svc.GetReferenceAsync(translated, ct);
        if (result is null)
        {
            return $"I couldn't find a drawing reference for \"{original}\". Try a different subject?";
        }

        var header = string.Equals(original, translated, StringComparison.OrdinalIgnoreCase)
            ? $"Drawing reference for \"{original}\":"
            : $"Drawing reference for \"{original}\" (searching: \"{translated}\"):";

        return header + "\n" +
               $"{result.Value.ImageUrl}\n" +
               $"Photo by {result.Value.PhotographerName} on Unsplash: {result.Value.PhotoPageUrl}";
    }

    private static string BuildActivitiesSkillPrompt(string? custom)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Skill: activities");
        sb.AppendLine("Suggest a small list of activities based on energy/mood signals in Personal context.");
        sb.AppendLine("Ignore calendar/events/emails unless they are part of Personal context.");
        sb.AppendLine("Prefer 3-7 bullet points with brief rationale.");
        sb.AppendLine("Do not quote the notes; use them only as signals.");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            sb.AppendLine();
            sb.AppendLine(custom.Trim());
        }
        return sb.ToString();
    }

    private static Task SendWithKeyboardAsync(ITelegramBotClient bot, long chatId, string text, CancellationToken cancellationToken)
    {
        return bot.SendTextMessageAsync(chatId, text, replyMarkup: BuildKeyboard(), cancellationToken: cancellationToken);
    }

    private static Task<List<ContextItem>> GetDailyItemsAsync(ContextService contextService, TimeZoneInfo tz, CancellationToken ct)
    {
        var (start, end) = TimeWindowHelper.GetDailyWindow(tz);
        // Pull enough rows to avoid a single source starving others.
        return contextService.GetForWindowAsync(start, end, take: 300, ct: ct);
    }

    private static Task<List<ContextItem>> GetWeeklyItemsAsync(ContextService contextService, TimeZoneInfo tz, CancellationToken ct)
    {
        var (start, end) = TimeWindowHelper.GetWeeklyWindow(tz);
        return contextService.GetForWindowAsync(start, end, take: 500, ct: ct);
    }

    private static string TruncateForTelegram(string text, int maxLen = 3500)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
        {
            return text;
        }

        return text[..maxLen] + "\n\n(truncated)";
    }

    private static string BuildUserFacingError(Exception ex)
    {
        // Keep this short; details are in logs.
        var message = ex.Message;
        if (message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            return "Summary failed: AI quota exceeded / billing issue (HTTP 429).";
        }

        if (message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "Summary failed: AI authentication error (check AI_API_KEY).";
        }

        return $"Summary failed: {message}";
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram bot error");
        return Task.CompletedTask;
    }

    private async Task HandleAddEventAsync(ITelegramBotClient bot, long chatId, string eventText, IServiceScope scope, CancellationToken ct)
    {
        var calendarService = scope.ServiceProvider.GetRequiredService<IGoogleCalendarEventService>();

        // Check if service is configured
        if (!calendarService.IsConfigured)
        {
            await SendWithKeyboardAsync(bot, chatId,
                "Google Calendar is not configured. Set GOOGLE_SERVICE_ACCOUNT_KEY_PATH or GOOGLE_SERVICE_ACCOUNT_JSON, and GOOGLE_CALENDAR_ID.",
                cancellationToken: ct);
            return;
        }

        await SendWithKeyboardAsync(bot, chatId, "Parsing your event...", cancellationToken: ct);

        // Clean up expired pending events (older than 5 minutes)
        var expiredChats = _pendingEventConfirmation
            .Where(kvp => (DateTimeOffset.UtcNow - kvp.Value.CreatedAt).TotalMinutes > 5)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var expiredChat in expiredChats)
        {
            _pendingEventConfirmation.TryRemove(expiredChat, out _);
        }

        var parser = scope.ServiceProvider.GetRequiredService<ICalendarEventParser>();
        var tzService = scope.ServiceProvider.GetRequiredService<TimeZoneService>();
        var tz = await tzService.GetTimeZoneInfoAsync(ct);

        _logger.LogInformation("Calendar event timezone resolved: {TimeZoneId} (baseUtcOffset={Offset})", tz.Id, tz.BaseUtcOffset);

        try
        {
            var parsed = await parser.ParseAsync(eventText, tz, ct);
            if (parsed is null)
            {
                await SendWithKeyboardAsync(bot, chatId,
                    "I couldn't understand that event. Try something like:\n\"Meeting with John tomorrow at 3pm for 1 hour\"",
                    cancellationToken: ct);
                return;
            }

            // Store pending event and show confirmation
            _pendingEventConfirmation[chatId] = new PendingCalendarEvent(parsed, DateTimeOffset.UtcNow);

            var preview = BuildEventPreview(parsed, tz);
            await bot.SendTextMessageAsync(chatId, preview,
                replyMarkup: BuildEventConfirmationKeyboard(),
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse calendar event");
            await SendWithKeyboardAsync(bot, chatId,
                TruncateForTelegram(BuildUserFacingError(ex)),
                cancellationToken: ct);
        }
    }

    private static string BuildEventPreview(ParsedCalendarEvent ev, TimeZoneInfo tz)
    {
        var localStart = TimeZoneInfo.ConvertTime(ev.StartTime, tz);
        var localEnd = TimeZoneInfo.ConvertTime(ev.StartTime + ev.Duration, tz);

        return $"Create this event?\n\n" +
               $"Title: {ev.Title}\n" +
               $"When: {localStart:ddd MMM d, h:mm tt} - {localEnd:h:mm tt}\n" +
               $"Duration: {FormatDuration(ev.Duration)}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 60)
        {
            return $"{(int)duration.TotalMinutes} min";
        }
        if (duration.TotalHours < 24)
        {
            var hours = (int)duration.TotalHours;
            var mins = duration.Minutes;
            return mins > 0 ? $"{hours}h {mins}min" : $"{hours}h";
        }
        return $"{duration.TotalHours:F1} hours";
    }

    private static InlineKeyboardMarkup BuildEventConfirmationKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Create Event", "calevent:confirm"),
                InlineKeyboardButton.WithCallbackData("Cancel", "calevent:reject")
            }
        });
    }
}
