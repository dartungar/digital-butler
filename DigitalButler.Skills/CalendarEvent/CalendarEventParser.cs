using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DigitalButler.Skills;

public sealed class ParsedCalendarEvent
{
    public string Title { get; init; } = string.Empty;
    public DateTimeOffset StartTime { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromHours(1);
    public string? Location { get; init; }
    public string? Description { get; init; }
    public string? TimeZone { get; init; }
    public double ConfidenceScore { get; init; }
}

public interface ICalendarEventParser
{
    Task<ParsedCalendarEvent?> ParseAsync(string naturalLanguageInput, TimeZoneInfo userTimeZone, CancellationToken ct = default);
}

public sealed class OpenAiCalendarEventParser : ICalendarEventParser
{
    private readonly HttpClient _httpClient;
    private readonly AiSettingsResolver _aiSettings;
    private readonly ILogger<OpenAiCalendarEventParser> _logger;

    private const string TaskName = "calendar-event-parsing";

    public OpenAiCalendarEventParser(
        HttpClient httpClient,
        AiSettingsResolver aiSettings,
        ILogger<OpenAiCalendarEventParser> logger)
    {
        _httpClient = httpClient;
        _aiSettings = aiSettings;
        _logger = logger;
    }

    public async Task<ParsedCalendarEvent?> ParseAsync(string naturalLanguageInput, TimeZoneInfo userTimeZone, CancellationToken ct = default)
    {
        var settings = await _aiSettings.ResolveAsync(TaskName, ct);
        if (settings is null || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            _logger.LogWarning("No AI settings configured for task {TaskName}", TaskName);
            return null;
        }

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, userTimeZone);
        var systemPrompt = BuildSystemPrompt(now, userTimeZone.Id);

        try
        {
            // Use the OpenAI Responses API (matches the rest of DigitalButler.Skills).
            // This avoids model-specific token parameter differences in legacy chat completions.
            var model = (settings.Model ?? "gpt-5-mini").Trim();
            const int maxOutputTokens = 800;

            var endpoint = OpenAiEndpoint.ResolveEndpoint(settings.BaseUrl);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model,
                instructions = systemPrompt,
                input = naturalLanguageInput,
                reasoning = new { effort = "low" },
                text = new { verbosity = "low" },
                max_output_tokens = maxOutputTokens
            }), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("AI API call failed: {StatusCode} {Response}", response.StatusCode, responseBody);
                return null;
            }

            using var doc = JsonDocument.Parse(responseBody);
            var messageContent = OpenAiEndpoint.ExtractResponsesText(doc.RootElement);

            if (string.IsNullOrWhiteSpace(messageContent))
            {
                _logger.LogWarning("Empty response from AI");
                return null;
            }

            // Extract JSON from the response (it might be wrapped in markdown code blocks)
            var jsonContent = ExtractJson(messageContent);
            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                _logger.LogWarning("Could not extract JSON from AI response: {Response}", messageContent);
                return null;
            }

            var parsed = JsonSerializer.Deserialize<ParsedEventResponse>(jsonContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed is null)
            {
                _logger.LogWarning("Failed to deserialize parsed event");
                return null;
            }

            if (!string.IsNullOrWhiteSpace(parsed.Error) || parsed.Confidence < 0.5)
            {
                _logger.LogInformation("Event parsing failed or low confidence: {Error}, confidence: {Confidence}", parsed.Error, parsed.Confidence);
                return null;
            }

            if (!TryParseStartTimeInUserTimeZone(parsed.StartIso, userTimeZone, out var startTime))
            {
                _logger.LogWarning("Failed to parse start time: {StartIso}", parsed.StartIso);
                return null;
            }

            return new ParsedCalendarEvent
            {
                Title = parsed.Title ?? "Untitled Event",
                StartTime = startTime,
                Duration = TimeSpan.FromMinutes(parsed.DurationMinutes > 0 ? parsed.DurationMinutes : 60),
                Location = parsed.Location,
                Description = parsed.Description,
                TimeZone = userTimeZone.Id,
                ConfidenceScore = parsed.Confidence
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse calendar event");
            return null;
        }
    }

    private static string BuildSystemPrompt(DateTimeOffset now, string timeZoneId)
    {
        return $$"""
            You are a calendar event parser. Extract event details from natural language.

            Current date/time: {{now:yyyy-MM-dd HH:mm}} ({{now.DayOfWeek}})
            User timezone: {{timeZoneId}}

            Parse the user's input and output ONLY valid JSON (no markdown, no explanation):
            {
              "title": "Event title",
                            "start_iso": "2024-01-15T15:00:00+03:00",
              "duration_minutes": 60,
              "location": null,
              "description": null,
              "confidence": 0.95
            }

            Guidelines:
            - If user says "tomorrow", calculate the actual date based on current date
            - If user says "next Monday", calculate the actual date
            - If no time is specified, default to 10:00
            - If no duration is specified, default to 60 minutes
            - start_iso MUST be in the user's timezone and MUST include the correct UTC offset (do NOT output UTC unless the user's timezone is UTC)
            - confidence should be 0.0-1.0, reflecting how certain you are about the parsing

            If you cannot parse the input (too vague, not an event, etc.), return:
            {"error": "reason", "confidence": 0}
            """;
    }

    private static bool TryParseStartTimeInUserTimeZone(string? startIso, TimeZoneInfo userTimeZone, out DateTimeOffset startTime)
    {
        startTime = default;
        if (string.IsNullOrWhiteSpace(startIso))
        {
            return false;
        }

        // Important: Many models output an ISO timestamp with a UTC offset even when the user spoke
        // in their local timezone. We interpret the *wall-clock* time in start_iso as being in the
        // user's timezone, then attach the correct offset for that timezone.

        if (DateTimeOffset.TryParse(startIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            var local = DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified);
            var offset = userTimeZone.GetUtcOffset(local);
            startTime = new DateTimeOffset(local, offset);
            return true;
        }

        if (DateTime.TryParse(startIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
        {
            var local = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            var offset = userTimeZone.GetUtcOffset(local);
            startTime = new DateTimeOffset(local, offset);
            return true;
        }

        return false;
    }

    private static string? ExtractJson(string text)
    {
        // Try to find JSON in markdown code block
        var jsonStart = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (jsonStart >= 0)
        {
            var contentStart = text.IndexOf('\n', jsonStart) + 1;
            var jsonEnd = text.IndexOf("```", contentStart, StringComparison.Ordinal);
            if (jsonEnd > contentStart)
            {
                return text[contentStart..jsonEnd].Trim();
            }
        }

        // Try plain code block
        jsonStart = text.IndexOf("```", StringComparison.Ordinal);
        if (jsonStart >= 0)
        {
            var contentStart = text.IndexOf('\n', jsonStart) + 1;
            var jsonEnd = text.IndexOf("```", contentStart, StringComparison.Ordinal);
            if (jsonEnd > contentStart)
            {
                return text[contentStart..jsonEnd].Trim();
            }
        }

        // Try to find raw JSON (starts with { and ends with })
        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return text[firstBrace..(lastBrace + 1)];
        }

        return null;
    }

    private sealed class ParsedEventResponse
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("start_iso")]
        public string? StartIso { get; set; }

        [JsonPropertyName("duration_minutes")]
        public int DurationMinutes { get; set; }

        [JsonPropertyName("location")]
        public string? Location { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
