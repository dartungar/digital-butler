using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalButler.Skills;

public sealed class UnsplashOptions
{
    public string? AccessKey { get; set; }
}

public readonly record struct DrawingReferenceResult(string ImageUrl, string PhotoPageUrl, string PhotographerName, string PhotographerProfileUrl);

public interface IRandomDrawingTopicService
{
    string GetRandomTopic();
}

public sealed class RandomDrawingTopicService : IRandomDrawingTopicService
{
    private static readonly string[] Topics =
    [
        // People & anatomy
        "hands", "portrait", "eyes", "lips", "nose", "ears", "feet", "figure drawing", "gesture", "face from side",
        // Animals
        "cat", "dog", "bird", "horse", "fish", "butterfly", "owl", "wolf", "rabbit", "elephant",
        // Objects
        "chair", "cup", "shoes", "book", "lamp", "bicycle", "car", "guitar", "clock", "vase",
        // Nature
        "tree", "flower", "clouds", "mountains", "waterfall", "leaves", "sunset", "rocks", "ocean waves", "forest",
        // Architecture
        "building", "window", "door", "bridge", "stairs", "interior", "cathedral", "cityscape", "street", "ruins",
        // Food
        "fruit", "vegetables", "bread", "coffee", "cake", "eggs", "cheese", "wine bottle", "kitchen utensils", "pie",
        // Fabric & texture
        "drapery", "fabric folds", "leather texture", "wood grain", "metal surface", "glass", "fur texture", "feathers", "rope", "paper",
        // Still life
        "still life with fruit", "vintage objects", "candles", "bottles and jars", "flowers in vase", "kitchen still life", "art supplies", "antique items", "musical instruments", "seashells"
    ];

    public string GetRandomTopic()
    {
        return Topics[Random.Shared.Next(Topics.Length)];
    }
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
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var accessKey = _options.AccessKey;
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            throw new InvalidOperationException("Unsplash access key is not configured (Unsplash:AccessKey / UNSPLASH_ACCESS_KEY).");
        }

        var query = subject.Trim();
        var url = "https://api.unsplash.com/search/photos" +
                  "?query=" + WebUtility.UrlEncode(query) +
                  "&per_page=30" +
                  "&content_filter=high" +
                  "&orientation=portrait";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"Client-ID {accessKey}");
        req.Headers.TryAddWithoutValidation("Accept-Version", "v1");

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Unsplash request failed: {Status} {Reason}. Body: {Body}", (int)resp.StatusCode, resp.ReasonPhrase, raw.Length <= 2000 ? raw : raw[..2000] + "…");
            throw new HttpRequestException($"Unsplash request failed with {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var resultsArray = results.EnumerateArray().ToArray();
        if (resultsArray.Length == 0)
        {
            return null;
        }

        // Pick a random result to provide variety when requesting "different image"
        var selected = resultsArray[Random.Shared.Next(resultsArray.Length)];
        if (selected.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var imageUrl = selected.GetProperty("urls").GetProperty("regular").GetString();
        var photoPageUrl = selected.GetProperty("links").GetProperty("html").GetString();
        var photographerName = selected.GetProperty("user").GetProperty("name").GetString();
        var photographerProfileUrl = selected.GetProperty("user").GetProperty("links").GetProperty("html").GetString();

        if (string.IsNullOrWhiteSpace(imageUrl) || string.IsNullOrWhiteSpace(photoPageUrl) || string.IsNullOrWhiteSpace(photographerName) || string.IsNullOrWhiteSpace(photographerProfileUrl))
        {
            return null;
        }

        return new DrawingReferenceResult(imageUrl, photoPageUrl, photographerName, photographerProfileUrl);
    }
}
