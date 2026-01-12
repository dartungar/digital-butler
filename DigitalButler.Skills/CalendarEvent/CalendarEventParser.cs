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
            var requestBody = new
            {
                model = settings.Model ?? "gpt-4o-mini",
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = naturalLanguageInput }
                },
                temperature = 0.1,
                max_tokens = 500
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var baseUrl = settings.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
            var endpoint = $"{baseUrl}/chat/completions";

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("AI API call failed: {StatusCode} {Response}", response.StatusCode, responseBody);
                return null;
            }

            var aiResponse = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody);
            var messageContent = aiResponse?.Choices?.FirstOrDefault()?.Message?.Content;

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

            if (!DateTimeOffset.TryParse(parsed.StartIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startTime))
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
            - Include timezone offset in start_iso based on user's timezone
            - confidence should be 0.0-1.0, reflecting how certain you are about the parsing

            If you cannot parse the input (too vague, not an event, etc.), return:
            {"error": "reason", "confidence": 0}
            """;
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

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; set; }

        public sealed class Choice
        {
            [JsonPropertyName("message")]
            public Message? Message { get; set; }
        }

        public sealed class Message
        {
            [JsonPropertyName("content")]
            public string? Content { get; set; }
        }
    }
}
