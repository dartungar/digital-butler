using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

var builder = Host.CreateApplicationBuilder(args);

var envOverrides = new Dictionary<string, string?>();
MapEnv("AI_BASE_URL", "AiDefaults:BaseUrl");
MapEnv("AI_MODEL", "AiDefaults:Model");
MapEnv("AI_API_KEY", "AiDefaults:ApiKey");
MapEnv("UNSPLASH_ACCESS_KEY", "Unsplash:AccessKey");
MapEnv("PEXELS_API_KEY", "Pexels:ApiKey");

if (envOverrides.Count > 0)
{
    builder.Configuration.AddInMemoryCollection(envOverrides);
}

builder.Services.Configure<AiDefaults>(builder.Configuration.GetSection("AiDefaults"));
builder.Services.Configure<UnsplashOptions>(builder.Configuration.GetSection("Unsplash"));
builder.Services.Configure<PexelsOptions>(builder.Configuration.GetSection("Pexels"));

builder.Services.AddSingleton<ChatStateManager>();
builder.Services.AddSingleton<IRandomDrawingTopicService, RandomDrawingTopicService>();
builder.Services.AddHttpClient<UnsplashDrawingReferenceService>();
builder.Services.AddHttpClient<PexelsDrawingReferenceService>();
builder.Services.AddScoped<ICompositeDrawingReferenceService, CompositeDrawingReferenceService>();
builder.Services.AddHttpClient<ISubjectTranslator, OpenAiSubjectTranslator>();

builder.Services.AddHostedService<DrawingBotService>();

await builder.Build().RunAsync();
return;

void MapEnv(string envVar, string configKey)
{
    var value = Environment.GetEnvironmentVariable(envVar);
    if (!string.IsNullOrWhiteSpace(value))
    {
        envOverrides[configKey] = value;
    }
}

public sealed class DrawingBotService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DrawingBotService> _logger;
    private readonly ChatStateManager _state;
    private readonly long _allowedUserId;
    private readonly ITelegramBotClient _bot;
    private int _offset;

    public DrawingBotService(IServiceProvider services, ILogger<DrawingBotService> logger, ChatStateManager state)
    {
        _services = services;
        _logger = logger;
        _state = state;

        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is required.");
        }

        var allowedUser = Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_USER_ID");
        if (string.IsNullOrWhiteSpace(allowedUser) || !long.TryParse(allowedUser, out _allowedUserId))
        {
            throw new InvalidOperationException("TELEGRAM_ALLOWED_USER_ID is required and must be a number.");
        }

        _bot = new TelegramBotClient(token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await _bot.GetMeAsync(stoppingToken);
        _logger.LogInformation("Drawing bot started as @{Username}", me.Username);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _bot.GetUpdatesAsync(
                    offset: _offset,
                    timeout: 30,
                    cancellationToken: stoppingToken);

                foreach (var update in updates)
                {
                    _offset = update.Id + 1;
                    await HandleUpdateAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Update polling failed; retrying soon");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task HandleUpdateAsync(Update update, CancellationToken ct)
    {
        if (update.Message is { } msg)
        {
            await HandleMessageAsync(msg, ct);
            return;
        }

        if (update.CallbackQuery is { } cb)
        {
            await HandleCallbackAsync(cb, ct);
        }
    }

    private async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        if (message.Text is null)
        {
            return;
        }

        var chatId = message.Chat.Id;
        var userId = message.From?.Id;
        var text = message.Text.Trim();

        if (userId != _allowedUserId)
        {
            await _bot.SendTextMessageAsync(chatId, "Unauthorized.", cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await _bot.SendTextMessageAsync(chatId, "Use /drawref <subject> to get a drawing reference.", cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/drawref", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/drawingref", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/drawing_reference", StringComparison.OrdinalIgnoreCase))
        {
            var subject = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(subject))
            {
                await SuggestRandomTopicAsync(chatId, ct);
                return;
            }

            await SendDrawingReferenceAsync(chatId, subject, null, ct);
            return;
        }

        if (DrawingSubjectParser.TryExtractSubject(text, out var extracted))
        {
            await SendDrawingReferenceAsync(chatId, extracted!, null, ct);
            return;
        }

        await _bot.SendTextMessageAsync(chatId, "Unknown command. Try /drawref hands", cancellationToken: ct);
    }

    private async Task HandleCallbackAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = callbackQuery.Message?.Chat.Id;
        var userId = callbackQuery.From.Id;
        var data = callbackQuery.Data;

        if (chatId is null || string.IsNullOrWhiteSpace(data))
        {
            await _bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        if (userId != _allowedUserId)
        {
            await _bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Unauthorized.", cancellationToken: ct);
            return;
        }

        if (!data.StartsWith("drawref:", StringComparison.Ordinal))
        {
            await _bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);
            return;
        }

        var action = data["drawref:".Length..];
        await _bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

        if (action == "confirm")
        {
            var topic = _state.GetAndRemovePendingTopic(chatId.Value);
            if (topic is null)
            {
                await _bot.SendTextMessageAsync(chatId.Value, "Session expired. Use /drawref again.", cancellationToken: ct);
                return;
            }

            await SendDrawingReferenceAsync(chatId.Value, topic, null, ct);
            return;
        }

        if (action == "another")
        {
            await SuggestRandomTopicAsync(chatId.Value, ct, editMessage: callbackQuery.Message);
            return;
        }

        if (action == "different_image")
        {
            var subject = _state.GetLastSubject(chatId.Value);
            if (string.IsNullOrWhiteSpace(subject))
            {
                await _bot.SendTextMessageAsync(chatId.Value, "No previous subject found. Try /drawref <subject>", cancellationToken: ct);
                return;
            }

            await SendDrawingReferenceAsync(chatId.Value, subject, null, ct);
            return;
        }

        if (action == "try_other_source")
        {
            var subject = _state.GetLastSubject(chatId.Value);
            if (string.IsNullOrWhiteSpace(subject))
            {
                await _bot.SendTextMessageAsync(chatId.Value, "No previous subject found. Try /drawref <subject>", cancellationToken: ct);
                return;
            }

            var lastSource = _state.GetLastSource(chatId.Value);
            var forcedSource = lastSource?.Equals("pexels", StringComparison.OrdinalIgnoreCase) == true ? "unsplash" : "pexels";
            await SendDrawingReferenceAsync(chatId.Value, subject, forcedSource, ct);
            return;
        }

        if (action == "different_subject")
        {
            await SuggestRandomTopicAsync(chatId.Value, ct);
        }
    }

    private async Task SuggestRandomTopicAsync(long chatId, CancellationToken ct, Message? editMessage = null)
    {
        using var scope = _services.CreateScope();
        var topicService = scope.ServiceProvider.GetRequiredService<IRandomDrawingTopicService>();
        var topic = topicService.GetRandomTopic();
        _state.SetPendingTopic(chatId, topic);

        if (editMessage is not null)
        {
            await _bot.EditMessageTextAsync(
                chatId,
                editMessage.MessageId,
                $"How about drawing: \"{topic}\"?",
                replyMarkup: BuildTopicKeyboard(),
                cancellationToken: ct);
            return;
        }

        await _bot.SendTextMessageAsync(
            chatId,
            $"How about drawing: \"{topic}\"?",
            replyMarkup: BuildTopicKeyboard(),
            cancellationToken: ct);
    }

    private async Task SendDrawingReferenceAsync(long chatId, string subject, string? forcedSource, CancellationToken ct)
    {
        await _bot.SendTextMessageAsync(chatId, "Finding a drawing reference...", cancellationToken: ct);

        try
        {
            using var scope = _services.CreateScope();
            var translator = scope.ServiceProvider.GetRequiredService<ISubjectTranslator>();
            var service = scope.ServiceProvider.GetRequiredService<ICompositeDrawingReferenceService>();

            var original = subject.Trim();
            var translated = await translator.TranslateToEnglishAsync(original, ct);
            if (string.IsNullOrWhiteSpace(translated))
            {
                translated = original;
            }

            DrawingReferenceResult? result;
            if (string.IsNullOrWhiteSpace(forcedSource))
            {
                result = await service.GetReferenceAsync(translated, ct);
            }
            else
            {
                var source = forcedSource.Equals("pexels", StringComparison.OrdinalIgnoreCase)
                    ? ImageSource.Pexels
                    : ImageSource.Unsplash;
                result = await service.GetReferenceFromSourceAsync(translated, source, ct);
            }

            if (result is null)
            {
                await _bot.SendTextMessageAsync(chatId, $"I couldn't find a drawing reference for \"{original}\".", cancellationToken: ct);
                return;
            }

            _state.SetLastSubject(chatId, original);
            _state.SetLastSource(chatId, result.Value.Source.ToString().ToLowerInvariant());

            var sourceName = result.Value.Source == ImageSource.Unsplash ? "Unsplash" : "Pexels";
            var header = string.Equals(original, translated, StringComparison.OrdinalIgnoreCase)
                ? $"Drawing reference for \"{original}\":"
                : $"Drawing reference for \"{original}\" (searching: \"{translated}\"):";

            var message = header + "\n" +
                          $"{result.Value.ImageUrl}\n" +
                          $"Photo by {result.Value.PhotographerName} on {sourceName}: {result.Value.PhotoPageUrl}";

            await _bot.SendTextMessageAsync(chatId, message, replyMarkup: BuildResultKeyboard(result.Value.Source), cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate drawing reference");
            await _bot.SendTextMessageAsync(chatId, $"Failed to generate drawing reference: {ex.Message}", cancellationToken: ct);
        }
    }

    private static InlineKeyboardMarkup BuildTopicKeyboard()
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

    private static InlineKeyboardMarkup BuildResultKeyboard(ImageSource currentSource)
    {
        var tryOtherLabel = currentSource == ImageSource.Unsplash ? "Try Pexels" : "Try Unsplash";

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Different Image", "drawref:different_image"),
                InlineKeyboardButton.WithCallbackData(tryOtherLabel, "drawref:try_other_source")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Different Subject", "drawref:different_subject")
            }
        });
    }
}

public sealed class ChatStateManager
{
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<long, ChatState> _states = new();

    public void SetPendingTopic(long chatId, string topic)
    {
        var state = _states.GetOrAdd(chatId, _ => new ChatState());
        state.PendingTopic = topic;
        state.PendingTopicAt = DateTimeOffset.UtcNow;
    }

    public string? GetAndRemovePendingTopic(long chatId)
    {
        if (!_states.TryGetValue(chatId, out var state))
        {
            return null;
        }

        if (state.PendingTopic is null || state.PendingTopicAt is null)
        {
            return null;
        }

        if (DateTimeOffset.UtcNow - state.PendingTopicAt.Value > _ttl)
        {
            state.PendingTopic = null;
            state.PendingTopicAt = null;
            return null;
        }

        var topic = state.PendingTopic;
        state.PendingTopic = null;
        state.PendingTopicAt = null;
        return topic;
    }

    public void SetLastSubject(long chatId, string subject)
    {
        var state = _states.GetOrAdd(chatId, _ => new ChatState());
        state.LastSubject = subject;
    }

    public string? GetLastSubject(long chatId)
    {
        return _states.TryGetValue(chatId, out var state) ? state.LastSubject : null;
    }

    public void SetLastSource(long chatId, string source)
    {
        var state = _states.GetOrAdd(chatId, _ => new ChatState());
        state.LastSource = source;
    }

    public string? GetLastSource(long chatId)
    {
        return _states.TryGetValue(chatId, out var state) ? state.LastSource : null;
    }

    private sealed class ChatState
    {
        public string? PendingTopic { get; set; }
        public DateTimeOffset? PendingTopicAt { get; set; }
        public string? LastSubject { get; set; }
        public string? LastSource { get; set; }
    }
}

public static class DrawingSubjectParser
{
    public static bool TryExtractSubject(string text, out string? subject)
    {
        subject = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lowered = text.Trim();

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

        var cleaned = tail.Trim().Trim('.', '!', '?', ':', ';', ',', '"', '\'', ')', '(', '[', ']', '{', '}');
        foreach (var stop in new[] { "some ", "a ", "an ", "the ", "my ", "any " })
        {
            if (cleaned.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[stop.Length..].Trim();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return false;
        }

        subject = cleaned;
        return true;
    }
}

public sealed class AiDefaults
{
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
}

public sealed class UnsplashOptions
{
    public string? AccessKey { get; set; }
}

public sealed class PexelsOptions
{
    public string? ApiKey { get; set; }
}

public enum ImageSource
{
    Unsplash,
    Pexels
}

public readonly record struct DrawingReferenceResult(
    string ImageUrl,
    string PhotoPageUrl,
    string PhotographerName,
    string PhotographerProfileUrl,
    ImageSource Source);

public interface IRandomDrawingTopicService
{
    string GetRandomTopic();
}

public sealed class RandomDrawingTopicService : IRandomDrawingTopicService
{
    private static readonly string[] Topics =
    [
        "hands", "portrait", "eyes", "nose", "ears", "figure drawing", "gesture drawing", "seated figure", "running pose",
        "cat sleeping", "dog portrait", "wolf", "fox", "owl", "eagle", "snake", "frog", "butterfly", "dragon",
        "chair", "teapot", "shoes", "watch", "guitar", "violin", "bicycle", "vintage car", "book", "chess pieces",
        "oak tree", "rose", "mountains", "waterfall", "clouds", "storm clouds", "ocean waves", "rocks", "castle", "bridge",
        "street scene", "cityscape", "apple", "banana", "tomato", "bread loaf", "coffee cup", "fabric folds", "wood grain",
        "still life with fruit", "candlelit scene", "bird's eye view", "winter snow scene", "moonlit night"
    ];

    public string GetRandomTopic() => Topics[Random.Shared.Next(Topics.Length)];
}

public interface IDrawingReferenceService
{
    Task<DrawingReferenceResult?> GetReferenceAsync(string subject, CancellationToken ct = default);
}

public sealed class UnsplashDrawingReferenceService : IDrawingReferenceService
{
    private readonly HttpClient _http;
    private readonly UnsplashOptions _options;
    private readonly ILogger<UnsplashDrawingReferenceService> _logger;

    public UnsplashDrawingReferenceService(HttpClient http, IOptions<UnsplashOptions> options, ILogger<UnsplashDrawingReferenceService> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DrawingReferenceResult?> GetReferenceAsync(string subject, CancellationToken ct = default)
    {
        var accessKey = _options.AccessKey;
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var url = "https://api.unsplash.com/search/photos" +
                  "?query=" + WebUtility.UrlEncode(subject.Trim()) +
                  "&per_page=30&content_filter=high&orientation=portrait";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"Client-ID {accessKey}");
        req.Headers.TryAddWithoutValidation("Accept-Version", "v1");

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Unsplash request failed: {Status} {Reason}", (int)resp.StatusCode, resp.ReasonPhrase);
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = results.EnumerateArray().ToArray();
        if (items.Length == 0)
        {
            return null;
        }

        var selected = items[Random.Shared.Next(items.Length)];
        var imageUrl = selected.GetProperty("urls").GetProperty("regular").GetString();
        var photoPageUrl = selected.GetProperty("links").GetProperty("html").GetString();
        var photographerName = selected.GetProperty("user").GetProperty("name").GetString();
        var photographerProfileUrl = selected.GetProperty("user").GetProperty("links").GetProperty("html").GetString();

        if (string.IsNullOrWhiteSpace(imageUrl) || string.IsNullOrWhiteSpace(photoPageUrl) ||
            string.IsNullOrWhiteSpace(photographerName) || string.IsNullOrWhiteSpace(photographerProfileUrl))
        {
            return null;
        }

        return new DrawingReferenceResult(imageUrl, photoPageUrl, photographerName, photographerProfileUrl, ImageSource.Unsplash);
    }
}

public sealed class PexelsDrawingReferenceService : IDrawingReferenceService
{
    private readonly HttpClient _http;
    private readonly PexelsOptions _options;
    private readonly ILogger<PexelsDrawingReferenceService> _logger;

    public PexelsDrawingReferenceService(HttpClient http, IOptions<PexelsOptions> options, ILogger<PexelsDrawingReferenceService> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DrawingReferenceResult?> GetReferenceAsync(string subject, CancellationToken ct = default)
    {
        var apiKey = _options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var url = "https://api.pexels.com/v1/search" +
                  "?query=" + WebUtility.UrlEncode(subject.Trim()) +
                  "&per_page=30&orientation=portrait";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", apiKey);

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Pexels request failed: {Status} {Reason}", (int)resp.StatusCode, resp.ReasonPhrase);
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("photos", out var photos) || photos.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = photos.EnumerateArray().ToArray();
        if (items.Length == 0)
        {
            return null;
        }

        var selected = items[Random.Shared.Next(items.Length)];
        var imageUrl = selected.GetProperty("src").GetProperty("large").GetString();
        var photoPageUrl = selected.GetProperty("url").GetString();
        var photographerName = selected.GetProperty("photographer").GetString();
        var photographerProfileUrl = selected.GetProperty("photographer_url").GetString();

        if (string.IsNullOrWhiteSpace(imageUrl) || string.IsNullOrWhiteSpace(photoPageUrl) ||
            string.IsNullOrWhiteSpace(photographerName) || string.IsNullOrWhiteSpace(photographerProfileUrl))
        {
            return null;
        }

        return new DrawingReferenceResult(imageUrl, photoPageUrl, photographerName, photographerProfileUrl, ImageSource.Pexels);
    }
}

public interface ICompositeDrawingReferenceService
{
    Task<DrawingReferenceResult?> GetReferenceAsync(string subject, CancellationToken ct = default);
    Task<DrawingReferenceResult?> GetReferenceFromSourceAsync(string subject, ImageSource source, CancellationToken ct = default);
}

public sealed class CompositeDrawingReferenceService : ICompositeDrawingReferenceService
{
    private readonly UnsplashDrawingReferenceService _unsplash;
    private readonly PexelsDrawingReferenceService _pexels;

    public CompositeDrawingReferenceService(UnsplashDrawingReferenceService unsplash, PexelsDrawingReferenceService pexels)
    {
        _unsplash = unsplash;
        _pexels = pexels;
    }

    public Task<DrawingReferenceResult?> GetReferenceAsync(string subject, CancellationToken ct = default)
    {
        var source = Random.Shared.Next(2) == 0 ? ImageSource.Unsplash : ImageSource.Pexels;
        return GetReferenceFromSourceAsync(subject, source, ct);
    }

    public async Task<DrawingReferenceResult?> GetReferenceFromSourceAsync(string subject, ImageSource source, CancellationToken ct = default)
    {
        var first = source == ImageSource.Unsplash ? await _unsplash.GetReferenceAsync(subject, ct) : await _pexels.GetReferenceAsync(subject, ct);
        if (first is not null)
        {
            return first;
        }

        return source == ImageSource.Unsplash
            ? await _pexels.GetReferenceAsync(subject, ct)
            : await _unsplash.GetReferenceAsync(subject, ct);
    }
}

public interface ISubjectTranslator
{
    Task<string> TranslateToEnglishAsync(string subject, CancellationToken ct = default);
}

public sealed class OpenAiSubjectTranslator : ISubjectTranslator
{
    private readonly HttpClient _httpClient;
    private readonly AiDefaults _settings;
    private readonly ILogger<OpenAiSubjectTranslator> _logger;

    public OpenAiSubjectTranslator(HttpClient httpClient, IOptions<AiDefaults> settings, ILogger<OpenAiSubjectTranslator> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<string> TranslateToEnglishAsync(string subject, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || string.IsNullOrWhiteSpace(_settings.Model))
        {
            return subject.Trim();
        }

        var endpoint = ResolveResponsesEndpoint(_settings.BaseUrl);
        var instructions = "Translate drawing subject to short English phrase. Output only translated text.";

        var body = new
        {
            model = _settings.Model,
            instructions,
            input = subject.Trim(),
            reasoning = new { effort = "low" },
            text = new { verbosity = "low" },
            max_output_tokens = 32
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        var rawBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Subject translation failed with status {Status}", (int)response.StatusCode);
            return subject.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var translated = ExtractResponsesText(doc.RootElement);
            if (string.IsNullOrWhiteSpace(translated))
            {
                return subject.Trim();
            }

            var firstLine = translated
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? translated;

            return firstLine.Trim().Trim('"', '\'', '.', ':', ';');
        }
        catch
        {
            return subject.Trim();
        }
    }

    private static string ResolveResponsesEndpoint(string? baseUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.Trim();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized += "/";
        }

        if (!normalized.EndsWith("v1/", StringComparison.OrdinalIgnoreCase))
        {
            normalized += "v1/";
        }

        return normalized + "responses";
    }

    private static string ExtractResponsesText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var c in content.EnumerateArray())
            {
                if (c.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    type.GetString() == "output_text" &&
                    c.TryGetProperty("text", out var textEl) &&
                    textEl.ValueKind == JsonValueKind.String)
                {
                    sb.AppendLine(textEl.GetString());
                }
            }
        }

        return sb.ToString().Trim();
    }
}
