using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DigitalButler.Modules;

public class OpenAiSummarizationService : ISummarizationService
{
    private readonly HttpClient _httpClient;
    private readonly AiSettingsResolver _resolver;

    private const string SystemPrompt = """
You are a digital butler.

You will receive context items grouped by source.
Each source may have additional instructions that apply ONLY to items from that source.
Do not apply one source's instructions to other sources.

Produce a single cohesive summary for the user.
""";

    public OpenAiSummarizationService(HttpClient httpClient, AiSettingsResolver resolver)
    {
        _httpClient = httpClient;
        _resolver = resolver;
    }

    public async Task<string> SummarizeAsync(IEnumerable<ContextItem> items, IReadOnlyDictionary<ContextSource, string> instructionsBySource, string taskName, CancellationToken ct = default)
    {
        var settings = await _resolver.ResolveAsync(taskName, ct);
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException("AI settings are not configured");
        }

        var prompt = BuildPrompt(items, instructionsBySource);
        var endpoint = ResolveEndpoint(settings.BaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
        var body = new
        {
            model = settings.Model,
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = prompt }
            }
        };
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"AI request failed with {(int)response.StatusCode} {response.ReasonPhrase} for {endpoint}. Body: {errorBody}");
        }
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var content = json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return content ?? string.Empty;
    }

    private static string ResolveEndpoint(string? baseUrl)
    {
        const string defaultEndpoint = "https://api.openai.com/v1/chat/completions";
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return defaultEndpoint;
        }

        var trimmed = baseUrl.Trim();

        // If caller provided a full endpoint already, use it.
        if (trimmed.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("/responses", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // Treat it as a base (host or host+v1).
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/chat/completions";
        }

        if (trimmed.EndsWith("/v1/", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "chat/completions";
        }

        if (trimmed.EndsWith("/", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "v1/chat/completions";
        }

        return trimmed + "/v1/chat/completions";
    }

    private static string BuildPrompt(IEnumerable<ContextItem> items, IReadOnlyDictionary<ContextSource, string> instructionsBySource)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Summarize the following context items.");
        sb.AppendLine("The items are grouped by source. Apply each source's instructions only within that source.");
        sb.AppendLine();

        foreach (var group in items.GroupBy(x => x.Source).OrderBy(g => g.Key))
        {
            sb.AppendLine($"Source: {group.Key}");

            if (instructionsBySource.TryGetValue(group.Key, out var perSource) && !string.IsNullOrWhiteSpace(perSource))
            {
                sb.AppendLine("Instructions for this source (apply ONLY to this source):");
                sb.AppendLine(perSource.Trim());
            }

            sb.AppendLine("Items:");
            foreach (var item in group)
            {
                sb.AppendLine($"- {item.Title}: {item.Body}");
                if (item.RelevantDate is not null)
                {
                    sb.AppendLine($"  Relevant: {item.RelevantDate:O}");
                }
            }

            sb.AppendLine();
        }
        return sb.ToString();
    }
}

