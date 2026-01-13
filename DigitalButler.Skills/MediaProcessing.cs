using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace DigitalButler.Skills;

public interface IMediaDownloadService
{
    Task<byte[]> DownloadFileAsync(ITelegramBotClient bot, string fileId, CancellationToken ct = default);
}

public sealed class TelegramMediaDownloadService : IMediaDownloadService
{
    private readonly ILogger<TelegramMediaDownloadService> _logger;

    public TelegramMediaDownloadService(ILogger<TelegramMediaDownloadService> logger)
    {
        _logger = logger;
    }

    public async Task<byte[]> DownloadFileAsync(ITelegramBotClient bot, string fileId, CancellationToken ct = default)
    {
        var file = await bot.GetFileAsync(fileId, ct);
        if (file.FilePath is null)
        {
            throw new InvalidOperationException($"File path is null for file ID {fileId}");
        }

        using var stream = new MemoryStream();
        await bot.DownloadFileAsync(file.FilePath, stream, ct);
        return stream.ToArray();
    }
}

public readonly record struct TranscriptionResult(string Text, float Confidence);

public interface IAudioTranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(byte[] audioData, string fileName, CancellationToken ct = default);
}

public sealed class OpenAiWhisperTranscriptionService : IAudioTranscriptionService
{
    private readonly HttpClient _httpClient;
    private readonly AiSettingsResolver _resolver;
    private readonly ILogger<OpenAiWhisperTranscriptionService> _logger;

    public OpenAiWhisperTranscriptionService(
        HttpClient httpClient,
        AiSettingsResolver resolver,
        ILogger<OpenAiWhisperTranscriptionService> logger)
    {
        _httpClient = httpClient;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<TranscriptionResult> TranscribeAsync(byte[] audioData, string fileName, CancellationToken ct = default)
    {
        var settings = await _resolver.ResolveAsync("audio-transcription", ct);

        // Whisper uses a different endpoint than Responses API
        // Build whisper endpoint from the base URL
        var baseUrl = settings.BaseUrl?.Trim() ?? "https://api.openai.com/v1/";

        // Normalize the base URL to get the /v1 prefix
        string whisperEndpoint;
        if (baseUrl.Contains("/v1/", StringComparison.OrdinalIgnoreCase))
        {
            // Extract everything up to and including /v1/
            var idx = baseUrl.IndexOf("/v1/", StringComparison.OrdinalIgnoreCase);
            whisperEndpoint = baseUrl[..(idx + 4)] + "audio/transcriptions";
        }
        else if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            whisperEndpoint = baseUrl + "/audio/transcriptions";
        }
        else
        {
            // Fallback: use the host with standard OpenAI path
            var uri = new Uri(baseUrl);
            whisperEndpoint = $"{uri.Scheme}://{uri.Host}/v1/audio/transcriptions";
        }

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(audioData), "file", fileName);
        content.Add(new StringContent("whisper-1"), "model");
        content.Add(new StringContent("json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, whisperEndpoint);
        request.Content = content;
        request.Headers.Add("Authorization", $"Bearer {settings.ApiKey}");

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var json = JsonDocument.Parse(responseBody);

        var text = json.RootElement.GetProperty("text").GetString() ?? string.Empty;

        // Whisper doesn't return confidence in basic response, default to 1.0
        return new TranscriptionResult(text.Trim(), 1.0f);
    }
}

public readonly record struct ImageAnalysisResult(string Description);

public interface IImageAnalysisService
{
    Task<ImageAnalysisResult> AnalyzeAsync(byte[] imageData, string? userCaption = null, CancellationToken ct = default);
}

public sealed class OpenAiVisionAnalysisService : IImageAnalysisService
{
    private readonly HttpClient _httpClient;
    private readonly AiSettingsResolver _resolver;
    private readonly ILogger<OpenAiVisionAnalysisService> _logger;

    public OpenAiVisionAnalysisService(
        HttpClient httpClient,
        AiSettingsResolver resolver,
        ILogger<OpenAiVisionAnalysisService> logger)
    {
        _httpClient = httpClient;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<ImageAnalysisResult> AnalyzeAsync(byte[] imageData, string? userCaption = null, CancellationToken ct = default)
    {
        var settings = await _resolver.ResolveAsync("image-analysis", ct);
        var endpoint = OpenAiEndpoint.ResolveEndpoint(settings.BaseUrl);

        var base64Image = Convert.ToBase64String(imageData);
        var imageUrl = $"data:image/jpeg;base64,{base64Image}";

        var promptText = string.IsNullOrWhiteSpace(userCaption)
            ? "Describe this image in detail. What are the key elements, objects, and context?"
            : $"Describe this image in detail. The user provided this caption: \"{userCaption}\". Expand on what you see in the image.";

        // Responses API requires input to be wrapped in a "message" type with content array
        var requestBody = new
        {
            model = settings.Model ?? "gpt-5-mini",
            instructions = "You are an image analyst. Describe images in detail.",
            input = new object[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = promptText
                        },
                        new
                        {
                            type = "input_image",
                            image_url = imageUrl
                        }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("Authorization", $"Bearer {settings.ApiKey}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Image analysis failed: {StatusCode} {Body}", response.StatusCode, errorBody);
            throw new HttpRequestException($"Image analysis failed: {response.StatusCode}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(responseBody);
        var description = OpenAiEndpoint.ExtractResponsesText(doc.RootElement);

        return new ImageAnalysisResult(description);
    }
}
