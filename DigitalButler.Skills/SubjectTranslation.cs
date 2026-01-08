using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DigitalButler.Skills;

public interface ISubjectTranslator
{
    Task<string> TranslateToEnglishAsync(string subject, CancellationToken ct = default);
}

public sealed class OpenAiSubjectTranslator : ISubjectTranslator
{
    private readonly HttpClient _httpClient;
    private readonly AiSettingsResolver _resolver;
    private readonly ILogger<OpenAiSubjectTranslator> _logger;

    public OpenAiSubjectTranslator(HttpClient httpClient, AiSettingsResolver resolver, ILogger<OpenAiSubjectTranslator> logger)
    {
        _httpClient = httpClient;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<string> TranslateToEnglishAsync(string subject, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return string.Empty;
        }

        // Always attempt translation when AI is configured.
        // If the input is already English, the model should return it unchanged.
        var settings = await _resolver.ResolveAsync("subject-translation", ct);
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.Model))
        {
            settings = await _resolver.ResolveAsync("on-demand-daily", ct);
        }
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.Model))
        {
            settings = await _resolver.ResolveAsync("daily-summary", ct);
        }
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.Model))
        {
            return subject.Trim();
        }

        var endpoint = OpenAiEndpoint.ResolveEndpoint(settings.BaseUrl);
        const int maxOutputTokens = 32;

        var instructions = """
You translate a drawing subject into English for image search.

Rules:
- Output ONLY the translated subject text (no quotes, no prefix, no explanations).
- Keep it short and concrete (2-6 words when possible).
- If the input is already English, output it unchanged.
- Do not add extra details that are not present in the input.

Examples:
Input: поза в полный рост
Output: full body pose

Input: яблоко
Output: apple

Input: hands
Output: hands
""";

        var body = new
        {
            model = settings.Model,
            instructions,
            input = subject.Trim(),
            reasoning = new { effort = "low" },
            text = new { verbosity = "low" },
            max_output_tokens = maxOutputTokens
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        var rawBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Subject translation request failed with {Status}. Falling back to original subject.", (int)response.StatusCode);
            return subject.Trim();
        }

        using var doc = JsonDocument.Parse(rawBody);
        var translated = OpenAiEndpoint.ExtractResponsesText(doc.RootElement);
        if (string.IsNullOrWhiteSpace(translated))
        {
            return subject.Trim();
        }

        var firstLine = translated
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? translated;

        return firstLine.Trim().Trim('"', '\'', '.', ':', ';');
    }
}
