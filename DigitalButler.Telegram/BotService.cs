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

namespace DigitalButler.Telegram;

public class BotService : Microsoft.Extensions.Hosting.IHostedService, IDisposable
{
    private readonly ILogger<BotService> _logger;
    private readonly IServiceProvider _services;
    private readonly string _token;
    private readonly long _allowedUserId;
    private readonly TimeSpan _startupPingTimeout;
    private TelegramBotClient? _bot;
    private CancellationTokenSource? _cts;
    private Task? _runLoop;

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
            try
            {
                _bot = new TelegramBotClient(_token);

                // Avoid hanging forever on startup (TLS/network issues). Keep it short and retry.
                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pingCts.CancelAfter(_startupPingTimeout);

                var me = await _bot.GetMeAsync(cancellationToken: pingCts.Token);
                _logger.LogInformation("Bot connected as {Username}", me.Username);

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
                _logger.LogWarning(
                    "Telegram bot startup ping cancelled/timed out after {TimeoutSeconds}s; retrying in {Delay}. Error: {ErrorType}",
                    (int)_startupPingTimeout.TotalSeconds,
                    delay,
                    ex.GetType().Name);

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
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
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
        var tzService = scope.ServiceProvider.GetRequiredService<TimeZoneService>();

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await SendWithKeyboardAsync(bot, chatId,
                "Commands:\n" +
                "/daily - today + timeless\n" +
                "/weekly - this week + timeless\n" +
                "/motivation - motivational message\n" +
                "/activities - activity suggestions\n" +
                "/add <text> - add personal context\n" +
                "/sync - run all ingests now",
                cancellationToken: ct);
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
                var summary = await ExecuteSummaryAsync(contextService, instructionService, skillInstructionService, summarizer, tzService, weekly: false, taskName: "on-demand-daily", ct);
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
                var summary = await ExecuteSummaryAsync(contextService, instructionService, skillInstructionService, summarizer, tzService, weekly: true, taskName: "on-demand-weekly", ct);
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
                var result = await ExecuteMotivationAsync(contextService, instructionService, skillInstructionService, summarizer, tzService, ct);
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
                var result = await ExecuteActivitiesAsync(contextService, instructionService, skillInstructionService, summarizer, tzService, ct);
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
                    var motivation = await ExecuteMotivationAsync(contextService, instructionService, skillInstructionService, summarizer, tzService, ct);
                    if (string.IsNullOrWhiteSpace(motivation)) motivation = "No motivation available.";
                    await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(motivation), cancellationToken: ct);
                    return;
                case ButlerSkill.Activities:
                    await SendWithKeyboardAsync(bot, chatId, "Generating activities...", cancellationToken: ct);
                    var activities = await ExecuteActivitiesAsync(contextService, instructionService, skillInstructionService, summarizer, tzService, ct);
                    if (string.IsNullOrWhiteSpace(activities)) activities = "No activities available.";
                    await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(activities), cancellationToken: ct);
                    return;
                case ButlerSkill.Summary:
                default:
                    await SendWithKeyboardAsync(bot, chatId, route.PreferWeeklySummary ? "Generating weekly summary..." : "Generating daily summary...", cancellationToken: ct);
                    var summary = await ExecuteSummaryAsync(contextService, instructionService, skillInstructionService, summarizer, tzService, weekly: route.PreferWeeklySummary, taskName: route.PreferWeeklySummary ? "on-demand-weekly" : "on-demand-daily", ct);
                    if (string.IsNullOrWhiteSpace(summary)) summary = "No summary available.";
                    await SendWithKeyboardAsync(bot, chatId, TruncateForTelegram(summary), cancellationToken: ct);
                    return;
            }
        }

        await SendWithKeyboardAsync(bot, chatId, "Unknown command. Use /daily, /weekly, /motivation, /activities, /add <text>", cancellationToken: ct);
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
        TimeZoneService tzService,
        CancellationToken ct)
    {
        var tz = await tzService.GetTimeZoneInfoAsync(ct);
        var items = await contextService.GetRelevantAsync(daysBack: 30, take: 250, ct: ct);

        var cfg = await GetSkillConfigAsync(skillInstructionService, ButlerSkill.Motivation, ct);
        var allowedMask = SkillContextDefaults.ResolveSourcesMask(ButlerSkill.Motivation, cfg?.ContextSourcesMask ?? -1);
        items = items.Where(x => ContextSourceMask.Contains(allowedMask, x.Source)).ToList();

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
        TimeZoneService tzService,
        CancellationToken ct)
    {
        var tz = await tzService.GetTimeZoneInfoAsync(ct);
        var items = await contextService.GetRelevantAsync(daysBack: 14, take: 250, ct: ct);

        var cfg = await GetSkillConfigAsync(skillInstructionService, ButlerSkill.Activities, ct);
        var allowedMask = SkillContextDefaults.ResolveSourcesMask(ButlerSkill.Activities, cfg?.ContextSourcesMask ?? -1);
        items = items.Where(x => ContextSourceMask.Contains(allowedMask, x.Source)).ToList();

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
}
