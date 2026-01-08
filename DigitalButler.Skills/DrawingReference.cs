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
                  "&per_page=1" +
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

        var first = results.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var imageUrl = first.GetProperty("urls").GetProperty("regular").GetString();
        var photoPageUrl = first.GetProperty("links").GetProperty("html").GetString();
        var photographerName = first.GetProperty("user").GetProperty("name").GetString();
        var photographerProfileUrl = first.GetProperty("user").GetProperty("links").GetProperty("html").GetString();

        if (string.IsNullOrWhiteSpace(imageUrl) || string.IsNullOrWhiteSpace(photoPageUrl) || string.IsNullOrWhiteSpace(photographerName) || string.IsNullOrWhiteSpace(photographerProfileUrl))
        {
            return null;
        }

        return new DrawingReferenceResult(imageUrl, photoPageUrl, photographerName, photographerProfileUrl);
    }
}
