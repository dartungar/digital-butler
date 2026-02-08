using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using DigitalButler.Common;

namespace DigitalButler.Skills;

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

    public async Task<string> SummarizeAsync(
        IEnumerable<ContextItem> items,
        IReadOnlyDictionary<ContextSource, string> instructionsBySource,
        string taskName,
        string? skillInstructions = null,
        CancellationToken ct = default)
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

        _logger.LogDebug("AI Request - Task: {Task}, Model: {Model}, SourceCount: {SourceCount}, TotalItems: {ItemCount}",
            taskName, settings.Model, groups.Length, items.Count());

        if (groups.Length == 0)
        {
            return string.Empty;
        }

        if (groups.Length == 1)
        {
            var group = groups[0];
            instructionsBySource.TryGetValue(group.Key, out var perSource);
            return await SummarizeOneSourceAsync(group.Key, group, perSource, skillInstructions, taskName, settings, ct);
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
                    var section = await SummarizeOneSourceAsync(group.Key, group, perSource, skillInstructions, taskName, settings, ct);
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
        string? skillInstructions,
        string taskName,
        AiSettings settings,
        CancellationToken ct)
    {
        var systemPrompt = BuildPerSourceSystemPrompt(source, perSourceInstructions, skillInstructions);
        var prompt = BuildPromptForOneSource(source, items, taskName);
        var endpoint = OpenAiEndpoint.ResolveEndpoint(settings.BaseUrl);
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
        var text = OpenAiEndpoint.ExtractResponsesText(root);

        string? incompleteReason = null;
        if (root.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("reason", out var reasonProp) && reasonProp.ValueKind == JsonValueKind.String)
        {
            incompleteReason = reasonProp.GetString();
        }

        return (text, rawBody, incompleteReason);
    }

    private static string TruncateForLogs(string value, int maxLen)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLen)
        {
            return value;
        }

        return value[..maxLen] + "…";
    }

    public async Task<string> SummarizeUnifiedAsync(
        IEnumerable<ContextItem> items,
        IReadOnlyDictionary<ContextSource, string> instructionsBySource,
        string taskName,
        string? skillInstructions = null,
        CancellationToken ct = default)
    {
        var settings = await _resolver.ResolveAsync(taskName, ct);
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException("AI settings are not configured");
        }

        var itemList = items.ToList();
        _logger.LogDebug("AI Request (unified) - Task: {Task}, Model: {Model}, TotalItems: {ItemCount}",
            taskName, settings.Model, itemList.Count);

        if (itemList.Count == 0)
        {
            return string.Empty;
        }

        var systemPrompt = BuildUnifiedSystemPrompt(instructionsBySource, skillInstructions);
        var prompt = BuildUnifiedPrompt(itemList, taskName);
        var endpoint = OpenAiEndpoint.ResolveEndpoint(settings.BaseUrl);
        if (!endpoint.Contains("/responses", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only the OpenAI Responses API is supported. Configure AI_BASE_URL (or task ProviderUrl) to point to '/v1/responses'. Resolved endpoint: {endpoint}");
        }

        var (text, rawBody, incompleteReason) = await SendResponsesAsync(endpoint, settings, systemPrompt, prompt, MaxOutputTokens, ct);
        if (string.IsNullOrWhiteSpace(text) && string.Equals(incompleteReason, "max_output_tokens", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Responses output truncated by max_output_tokens; retrying with higher limit {RetryMax} (model={Model})", RetryMaxOutputTokens, settings.Model);
            (text, rawBody, _) = await SendResponsesAsync(endpoint, settings, systemPrompt, prompt, RetryMaxOutputTokens, ct);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Responses API returned empty text output. Body (truncated): {Body}", TruncateForLogs(rawBody, 2000));
            throw new InvalidOperationException("AI response contained no text output (Responses API). See server logs for the raw response body.");
        }

        return text;
    }

    private static string BuildUnifiedSystemPrompt(
        IReadOnlyDictionary<ContextSource, string> instructionsBySource,
        string? skillInstructions)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BaseSystemPrompt.Trim());
        sb.AppendLine();
        sb.AppendLine("You will receive context items from multiple sources combined.");

        if (!string.IsNullOrWhiteSpace(skillInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("Skill instructions:");
            sb.AppendLine(skillInstructions.Trim());
        }

        foreach (var (source, instructions) in instructionsBySource)
        {
            if (!string.IsNullOrWhiteSpace(instructions))
            {
                sb.AppendLine();
                sb.AppendLine($"Custom instructions for {source}:");
                sb.AppendLine(instructions.Trim());
            }
        }

        return sb.ToString().Trim();
    }

    private static string BuildUnifiedPrompt(List<ContextItem> items, string taskName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Summarize the following context items into a single unified summary.");
        sb.AppendLine("Merge information from all sources naturally. Do NOT separate by source.");
        sb.AppendLine();
        sb.AppendLine("Items:");

        var groups = items.GroupBy(x => x.Source).OrderBy(g => g.Key);
        foreach (var group in groups)
        {
            sb.AppendLine($"[Source: {group.Key}]");
            foreach (var item in group)
            {
                var title = TruncateForPrompt(item.Title, MaxTitleCharsInPrompt);
                var body = TruncateForPrompt(CompressBodyForPrompt(item.Body), MaxBodyCharsInPrompt);
                sb.AppendLine($"- {title}: {body}");
                if (item.RelevantDate is not null)
                {
                    sb.AppendLine($"  Relevant: {item.RelevantDate:O}");
                }
            }
        }

        return sb.ToString();
    }

    private static string BuildPromptForOneSource(ContextSource source, IEnumerable<ContextItem> items, string taskName)
    {
        // Motivation/activities are skills, not summaries. Don't force a "Summarize" instruction.
        // We still keep the same input shape (items list), but the task description changes.
        var isMotivation = taskName.Equals("motivation", StringComparison.OrdinalIgnoreCase);
        var isActivities = taskName.Equals("activities", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();

        if (isMotivation)
        {
            sb.AppendLine("Write a short motivational message for the user.");
            sb.AppendLine("Ground it in the personal context items below, but do NOT quote or list the items.");
            sb.AppendLine("Do not output meta-commentary like 'contains' or 'the notes say'.");
            sb.AppendLine("Keep it concise (3-8 sentences), no bullet list.");
        }
        else if (isActivities)
        {
            sb.AppendLine("Suggest what the user could do next.");
            sb.AppendLine("Ground it in the personal context items below.");
            sb.AppendLine("Return 3-7 bullet points, each with a short rationale.");
            sb.AppendLine("Do not quote the items verbatim.");
        }
        else
        {
            sb.AppendLine($"Summarize the following context items from source: {source}.");
            sb.AppendLine("Return ONLY the summary text for this source (no headings).");
        }

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

    private static string BuildPerSourceSystemPrompt(ContextSource source, string? perSourceInstructions, string? skillInstructions)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BaseSystemPrompt.Trim());
        sb.AppendLine();
        sb.AppendLine($"Source: {source}");

        if (!string.IsNullOrWhiteSpace(skillInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("Skill instructions:");
            sb.AppendLine(skillInstructions.Trim());
        }

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
