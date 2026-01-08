using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using DigitalButler.Common;

namespace DigitalButler.Skills;

public interface IAiContextAugmenter
{
    Task<string> GenerateAsync(ButlerSkill skill, IReadOnlyList<ContextItem> items, string taskName, CancellationToken ct = default);
}

/// <summary>
/// Generates an extra short "self thought" snippet for a skill.
/// This snippet is then injected as an additional context item.
/// </summary>
public sealed class OpenAiContextAugmenter : IAiContextAugmenter
{
    private readonly HttpClient _httpClient;
    private readonly AiSettingsResolver _resolver;
    private readonly ILogger<OpenAiContextAugmenter> _logger;

    // Keep this small: it's auxiliary context, not the final answer.
    private const int MaxOutputTokens = 320;
    private const int MaxItemsInPrompt = 20;
    private const int MaxTitleChars = 140;
    private const int MaxBodyChars = 400;

    public OpenAiContextAugmenter(HttpClient httpClient, AiSettingsResolver resolver, ILogger<OpenAiContextAugmenter> logger)
    {
        _httpClient = httpClient;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<string> GenerateAsync(ButlerSkill skill, IReadOnlyList<ContextItem> items, string taskName, CancellationToken ct = default)
    {
        // Prefer a dedicated task, but fall back to the current taskName and then on-demand daily.
        var settings = await _resolver.ResolveAsync("ai-context", ct);
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.Model))
        {
            settings = await _resolver.ResolveAsync(taskName, ct);
        }
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
            // No AI config; silently skip augmentation.
            return string.Empty;
        }

        var endpoint = OpenAiEndpoint.ResolveEndpoint(settings.BaseUrl);
        if (!endpoint.Contains("/responses", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only the OpenAI Responses API is supported. Configure AI_BASE_URL (or task ProviderUrl) to point to '/v1/responses'. Resolved endpoint: {endpoint}");
        }

        var systemPrompt = """
You are a digital butler's inner reflection engine.

You receive context items about the user.
Generate a short additional thought that could help the user for the given skill.

Rules:
- Do NOT quote the items verbatim.
- Do NOT invent concrete facts (names, dates, meetings) that are not present.
- It's OK to add general insight, encouragement, or suggestions.
- Output plain text only.
- Keep it short: 2-5 sentences.
- Do not mention that you are an AI or that you were given context items.
""";

        var input = BuildInput(skill, items);

        var body = new
        {
            model = settings.Model,
            instructions = systemPrompt,
            input,
            reasoning = new { effort = "low" },
            text = new { verbosity = "low" },
            max_output_tokens = MaxOutputTokens
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        var rawBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"AI context augmentation request failed with {(int)response.StatusCode} {response.ReasonPhrase} for {endpoint}. Body: {rawBody}");
        }

        using var doc = JsonDocument.Parse(rawBody);
        var text = OpenAiEndpoint.ExtractResponsesText(doc.RootElement);
        text = (text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("AI context augmentation returned empty text (skill={Skill}, task={Task}).", skill, taskName);
            return string.Empty;
        }

        // Safety: keep it compact so we don't bloat downstream prompts.
        if (text.Length > 900)
        {
            text = text[..900].Trim();
        }

        return text;
    }

    private static string BuildInput(ButlerSkill skill, IReadOnlyList<ContextItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Skill: {skill}");
        sb.AppendLine();
        sb.AppendLine("Context items:");

        foreach (var item in items.Take(MaxItemsInPrompt))
        {
            var title = Truncate(item.Title, MaxTitleChars);
            var body = Truncate(Compress(item.Body), MaxBodyChars);
            sb.AppendLine($"- {title}: {body}");
        }

        return sb.ToString();
    }

    private static string Compress(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string Truncate(string value, int maxLen)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLen) return value;
        return value[..maxLen] + "…";
    }
}
