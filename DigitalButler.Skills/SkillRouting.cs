using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using DigitalButler.Common;

namespace DigitalButler.Skills;

public readonly record struct SkillRoute(ButlerSkill Skill, bool PreferWeeklySummary);

public interface ISkillRouter
{
    Task<SkillRoute> RouteAsync(string text, CancellationToken ct = default);
}

public sealed class OpenAiSkillRouter : ISkillRouter
{
    private readonly HttpClient _httpClient;
    private readonly AiSettingsResolver _resolver;
    private readonly ILogger<OpenAiSkillRouter> _logger;

    public OpenAiSkillRouter(HttpClient httpClient, AiSettingsResolver resolver, ILogger<OpenAiSkillRouter> logger)
    {
        _httpClient = httpClient;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<SkillRoute> RouteAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SkillRoute(ButlerSkill.Summary, PreferWeeklySummary: false);
        }

        // Skill routing needs AI settings. Prefer task-specific configuration, but if this task
        // isn't configured (common when AI defaults aren't set and only per-task settings exist),
        // fall back to the same settings used by on-demand summaries.
        var settings = await _resolver.ResolveAsync("skill-routing", ct);
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
            return new SkillRoute(ButlerSkill.Summary, PreferWeeklySummary: false);
        }

        var endpoint = OpenAiEndpoint.ResolveEndpoint(settings.BaseUrl);
        var systemPrompt = """
You are a strict router for a digital butler.

Pick exactly one skill for the user's message:
- summary (daily/weekly agenda)
- motivation (motivational message based on personal context)
- activities (suggest what to do based on energy/mood)
- drawing_reference (find a drawing reference image)

Return ONLY one of these tokens:
summary_daily
summary_weekly
motivation
activities
drawing_reference

Output rules:
- Output the token only (no other words like "Answer:" and no punctuation).
- Lowercase.
- Do NOT abbreviate.

Examples:
User: get me a daily summary
Answer: summary_daily

User: weekly summary please
Answer: summary_weekly

User: what's on my calendar today?
Answer: summary_daily

User: motivate me
Answer: motivation

User: what should I do tonight?
Answer: activities

User: I want a drawing reference for hands
Answer: drawing_reference

User: I want to practice drawing
Answer: drawing_reference
""";

        var input = "User message:\n" + text.Trim();

        try
        {
            var (token, raw) = await SendResponsesAsync(endpoint, settings, systemPrompt, input, ct);
            if (TryParseRouteToken(token, out var route))
            {
                _logger.LogInformation("Skill routing chose {Skill} (weekly={Weekly}) for message length {Len}", route.Skill, route.PreferWeeklySummary, text.Length);
                return route;
            }

            // Important: never attempt to infer from the raw JSON body (it can contain unrelated words).
            var rawPreview = raw.Length <= 4000 ? raw : raw[..4000] + "…";
            _logger.LogWarning("Skill router raw response (truncated): {Raw}", rawPreview);

            _logger.LogWarning("Skill router returned unexpected token '{Token}'. Defaulting to daily summary.", token);
            return new SkillRoute(ButlerSkill.Summary, PreferWeeklySummary: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skill routing failed; defaulting to daily summary");
            return new SkillRoute(ButlerSkill.Summary, PreferWeeklySummary: false);
        }
    }

    private static bool TryParseRouteToken(string output, out SkillRoute route)
    {
        route = default;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        // Normalize: take first non-empty line, trim, strip punctuation.
        var firstLine = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? output;

        var token = firstLine.Trim().Trim('"', '\'', '.', ':', ';', ',', ')', '(', '[', ']', '{', '}').ToLowerInvariant();

        // If the model wrapped the token (e.g. "Answer: summary_daily"), search for allowed tokens.
        if (token.Contains("summary_daily")) token = "summary_daily";
        else if (token.Contains("summary_weekly")) token = "summary_weekly";
        else if (token.Contains("motivation")) token = "motivation";
        else if (token.Contains("activities")) token = "activities";
        else if (token.Contains("drawing_reference")) token = "drawing_reference";

        // Be tolerant of minor deviations (we still instruct the model not to abbreviate).
        if (token is "mot" or "motiv" or "motivational") token = "motivation";
        if (token is "act" or "activity") token = "activities";
        if (token is "draw" or "drawing" or "reference" or "drawingreference" or "drawing_ref") token = "drawing_reference";
        if (token is "summary") token = "summary_daily";

        switch (token)
        {
            case "motivation":
                route = new SkillRoute(ButlerSkill.Motivation, PreferWeeklySummary: false);
                return true;
            case "activities":
                route = new SkillRoute(ButlerSkill.Activities, PreferWeeklySummary: false);
                return true;
            case "drawing_reference":
                route = new SkillRoute(ButlerSkill.DrawingReference, PreferWeeklySummary: false);
                return true;
            case "summary_weekly":
                route = new SkillRoute(ButlerSkill.Summary, PreferWeeklySummary: true);
                return true;
            case "summary_daily":
                route = new SkillRoute(ButlerSkill.Summary, PreferWeeklySummary: false);
                return true;
            default:
                return false;
        }
    }

    private async Task<(string token, string rawBody)> SendResponsesAsync(string endpoint, AiSettings settings, string instructions, string input, CancellationToken ct)
    {
        // Routing should be cheap, but some models can return only a reasoning item when the output cap is too low.
        // Use a modest cap and retry once if Responses reports truncation.
        const int maxOutputTokens = 256;
        const int retryMaxOutputTokens = 512;

        var body = new
        {
            model = settings.Model,
            instructions,
            input,
            reasoning = new { effort = "low" },
            text = new { verbosity = "low" },
            max_output_tokens = maxOutputTokens
        };

        HttpRequestMessage CreateRequest()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return request;
        }

        // Retry once on transient 5xx to avoid defaulting to daily summary due to provider flakiness.
        // Also retry once on max_output_tokens truncation if the provider returns no text.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = CreateRequest();
            using var response = await _httpClient.SendAsync(request, ct);
            var rawBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                if (attempt == 0 && statusCode >= 500)
                {
                    _logger.LogWarning("Skill routing request failed with {Status}. Retrying once.", statusCode);
                    await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
                    continue;
                }

                throw new HttpRequestException($"Skill routing AI request failed with {statusCode} {response.ReasonPhrase}. Body: {rawBody}");
            }

            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            var token = OpenAiEndpoint.ExtractResponsesText(root);

            string? incompleteReason = null;
            if (root.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object &&
                details.TryGetProperty("reason", out var reasonProp) && reasonProp.ValueKind == JsonValueKind.String)
            {
                incompleteReason = reasonProp.GetString();
            }

            // If we got no token and the response was truncated, retry once with a higher cap.
            if (attempt == 0 && string.IsNullOrWhiteSpace(token) &&
                string.Equals(incompleteReason, "max_output_tokens", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Skill routing response truncated by max_output_tokens; retrying once with higher limit {RetryMax}.", retryMaxOutputTokens);

                var retryBody = new
                {
                    model = settings.Model,
                    instructions,
                    input,
                    reasoning = new { effort = "low" },
                    text = new { verbosity = "low" },
                    max_output_tokens = retryMaxOutputTokens
                };

                HttpRequestMessage CreateRetryRequest()
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
                    req.Content = new StringContent(JsonSerializer.Serialize(retryBody), Encoding.UTF8, "application/json");
                    return req;
                }

                using var retryRequest = CreateRetryRequest();
                using var retryResponse = await _httpClient.SendAsync(retryRequest, ct);
                var retryRawBody = await retryResponse.Content.ReadAsStringAsync(ct);
                if (!retryResponse.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Skill routing AI retry request failed with {(int)retryResponse.StatusCode} {retryResponse.ReasonPhrase}. Body: {retryRawBody}");
                }

                using var retryDoc = JsonDocument.Parse(retryRawBody);
                var retryToken = OpenAiEndpoint.ExtractResponsesText(retryDoc.RootElement);
                return (retryToken, retryRawBody);
            }

            return (token, rawBody);
        }

        // Unreachable, but required for compilation.
        return (string.Empty, string.Empty);
    }
}

/// <summary>
/// Shared helper for OpenAI Responses API endpoint resolution and text extraction.
/// Used by both OpenAiSummarizationService and OpenAiSkillRouter.
/// </summary>
public static class OpenAiEndpoint
{
    public static string ResolveEndpoint(string? baseUrl)
    {
        const string defaultEndpoint = "https://api.openai.com/v1/responses";
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return defaultEndpoint;
        }

        var trimmed = baseUrl.Trim();
        if (trimmed.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("/v1/chat/completions is not supported. Use the OpenAI Responses API endpoint: /v1/responses");
        }

        if (trimmed.Contains("/responses", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/responses";
        }

        if (trimmed.EndsWith("/v1/", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "responses";
        }

        if (trimmed.EndsWith("/", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "v1/responses";
        }

        return trimmed + "/v1/responses";
    }

    public static string ExtractResponsesText(JsonElement json)
    {
        // Newer Responses API often includes a convenience string.
        if (json.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString() ?? string.Empty;
        }

        // Some implementations return a different envelope; attempt best-effort fallback.
        // (We still require using /responses; this is just parsing.)
        if (json.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            try
            {
                var content = choices[0].GetProperty("message").GetProperty("content").GetString();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return content;
                }
            }
            catch
            {
                // ignore and continue
            }
        }

        // Otherwise, walk the structured output.
        if (!json.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            // Some variants can return output items directly as output_text.
            if (item.TryGetProperty("type", out var itemType) && itemType.ValueKind == JsonValueKind.String &&
                string.Equals(itemType.GetString(), "output_text", StringComparison.OrdinalIgnoreCase))
            {
                var direct = TryExtractText(item);
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(direct.Trim());
                }
                continue;
            }

            if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    var t = TryExtractText(part);
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(t.Trim());
                    }
                }
            }
        }

        return sb.ToString().Trim();
    }

    private static string? TryExtractText(JsonElement part)
    {
        // Typical: {"type":"output_text","text":"..."}
        if (part.TryGetProperty("text", out var text))
        {
            if (text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }

            // Some schemas nest: {"text": {"value": "..."}}
            if (text.ValueKind == JsonValueKind.Object && text.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        // Alternative: {"type":"output_text","content":"..."}
        if (part.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        return null;
    }
}
