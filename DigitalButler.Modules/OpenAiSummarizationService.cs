using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DigitalButler.Modules;

public class OpenAiSummarizationService : ISummarizationService
{
    private readonly HttpClient _httpClient;
    private readonly AiSettingsResolver _resolver;
    private readonly ILogger<OpenAiSummarizationService> _logger;

    // Performance knobs: large calendars can create huge prompts and slow responses.
    private const int MaxTitleCharsInPrompt = 200;
    private const int MaxBodyCharsInPrompt = 800;
    private const int MaxOutputTokens = 2000;
    private const int RetryMaxOutputTokens = 4000;

    private const string BaseSystemPrompt = """
You are a digital butler.

You will receive context items from exactly one source.
Apply any provided custom instructions ONLY to this source.
Do not invent items that are not present.
""";

    public OpenAiSummarizationService(HttpClient httpClient, AiSettingsResolver resolver, ILogger<OpenAiSummarizationService> logger)
    {
        _httpClient = httpClient;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<string> SummarizeAsync(IEnumerable<ContextItem> items, IReadOnlyDictionary<ContextSource, string> instructionsBySource, string taskName, CancellationToken ct = default)
    {
        var settings = await _resolver.ResolveAsync(taskName, ct);
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException("AI settings are not configured");
        }

        // To prevent instruction/style leakage across sources, summarize each source independently
        // and stitch the results together.
        var groups = items
            .GroupBy(x => x.Source)
            .OrderBy(g => g.Key)
            .ToArray();

        if (groups.Length == 0)
        {
            return string.Empty;
        }

        if (groups.Length == 1)
        {
            var group = groups[0];
            instructionsBySource.TryGetValue(group.Key, out var perSource);
            return await SummarizeOneSourceAsync(group.Key, group, perSource, settings, ct);
        }

        // Run per-source AI calls in parallel (bounded) to reduce end-to-end latency.
        // Keep concurrency small to avoid hitting provider rate limits.
        const int maxConcurrency = 2;
        using var throttle = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = groups
            .Select(async (group, index) =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    instructionsBySource.TryGetValue(group.Key, out var perSource);
                    var section = await SummarizeOneSourceAsync(group.Key, group, perSource, settings, ct);
                    return (index, source: group.Key, section);
                }
                finally
                {
                    throttle.Release();
                }
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var sb = new StringBuilder();
        foreach (var result in results.OrderBy(x => x.index))
        {
            if (string.IsNullOrWhiteSpace(result.section))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
            }

            sb.AppendLine(FormatSourceHeading(result.source));
            sb.AppendLine(result.section.Trim());
        }

        return sb.ToString().Trim();
    }

    private async Task<string> SummarizeOneSourceAsync(
        ContextSource source,
        IEnumerable<ContextItem> items,
        string? perSourceInstructions,
        AiSettings settings,
        CancellationToken ct)
    {
        var systemPrompt = BuildPerSourceSystemPrompt(source, perSourceInstructions);
        var prompt = BuildPromptForOneSource(source, items);
        var endpoint = ResolveEndpoint(settings.BaseUrl);
        if (!endpoint.Contains("/responses", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only the OpenAI Responses API is supported. Configure AI_BASE_URL (or task ProviderUrl) to point to '/v1/responses'. Resolved endpoint: {endpoint}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);

        var (text, rawBody, incompleteReason) = await SendResponsesAsync(endpoint, settings, systemPrompt, prompt, MaxOutputTokens, ct);
        if (string.IsNullOrWhiteSpace(text) && string.Equals(incompleteReason, "max_output_tokens", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Responses output truncated by max_output_tokens; retrying with higher limit {RetryMax} (model={Model}, source={Source})", RetryMaxOutputTokens, settings.Model, source);
            (text, rawBody, _) = await SendResponsesAsync(endpoint, settings, systemPrompt, prompt, RetryMaxOutputTokens, ct);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Responses API returned empty text output. Body (truncated): {Body}", TruncateForLogs(rawBody, 2000));
            throw new InvalidOperationException("AI response contained no text output (Responses API). See server logs for the raw response body.");
        }

        return text;
    }

    private async Task<(string text, string rawBody, string? incompleteReason)> SendResponsesAsync(
        string endpoint,
        AiSettings settings,
        string systemPrompt,
        string prompt,
        int maxOutputTokens,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);

        var body = new
        {
            model = settings.Model,
            instructions = systemPrompt,
            input = prompt,

            // Keep reasoning small so we actually get visible text within the cap.
            reasoning = new { effort = "low" },
            text = new { verbosity = "low" },

            max_output_tokens = maxOutputTokens
        };
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        var rawBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"AI request failed with {(int)response.StatusCode} {response.ReasonPhrase} for {endpoint}. Body: {rawBody}");
        }

        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        var text = ExtractResponsesText(root);

        string? incompleteReason = null;
        if (root.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("reason", out var reasonProp) && reasonProp.ValueKind == JsonValueKind.String)
        {
            incompleteReason = reasonProp.GetString();
        }

        return (text, rawBody, incompleteReason);
    }

    private static string ResolveEndpoint(string? baseUrl)
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

        // If caller provided a full endpoint already, use it.
        if (trimmed.Contains("/responses", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // Treat it as a base (host or host+v1).
        // Default to responses.
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

    private static string ExtractResponsesText(JsonElement json)
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

    private static string TruncateForLogs(string value, int maxLen)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLen)
        {
            return value;
        }

        return value[..maxLen] + "…";
    }

    private static string BuildPromptForOneSource(ContextSource source, IEnumerable<ContextItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Summarize the following context items from source: {source}.");
        sb.AppendLine("Return ONLY the summary text for this source (no headings).");
        sb.AppendLine();
        sb.AppendLine("Items:");
        foreach (var item in items)
        {
            var title = TruncateForPrompt(item.Title, MaxTitleCharsInPrompt);
            var body = TruncateForPrompt(CompressBodyForPrompt(item.Body), MaxBodyCharsInPrompt);
            sb.AppendLine($"- {title}: {body}");
            if (item.RelevantDate is not null)
            {
                sb.AppendLine($"  Relevant: {item.RelevantDate:O}");
            }
        }
        return sb.ToString();
    }

    private static string CompressBodyForPrompt(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        // Calendar items can include long descriptions; keep the header-ish lines only.
        // If there is a blank line, drop everything after it.
        var trimmed = body.Trim();
        var blankLineIdx = trimmed.IndexOf("\n\n", StringComparison.Ordinal);
        if (blankLineIdx > 0)
        {
            return trimmed[..blankLineIdx].Trim();
        }

        return trimmed;
    }

    private static string TruncateForPrompt(string value, int maxLen)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLen)
        {
            return value;
        }

        return value[..maxLen] + "…";
    }

    private static string BuildPerSourceSystemPrompt(ContextSource source, string? perSourceInstructions)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BaseSystemPrompt.Trim());
        sb.AppendLine();
        sb.AppendLine($"Source: {source}");

        if (!string.IsNullOrWhiteSpace(perSourceInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("Custom instructions for this source:");
            sb.AppendLine(perSourceInstructions.Trim());
        }

        return sb.ToString().Trim();
    }

    private static string FormatSourceHeading(ContextSource source)
    {
        // Keep headings short and predictable for Telegram.
        return source switch
        {
            ContextSource.GoogleCalendar => "Google Calendar",
            ContextSource.Gmail => "Gmail",
            ContextSource.Personal => "Personal",
            ContextSource.Other => "Other",
            _ => source.ToString()
        };
    }
}

