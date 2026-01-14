using DigitalButler.Context;
using DigitalButler.Skills;
using Microsoft.Extensions.Logging;

namespace DigitalButler.Telegram.Skills;

public sealed class CalendarEventSkillExecutor : ICalendarEventSkillExecutor
{
    private readonly IGoogleCalendarEventService _calendarService;
    private readonly ICalendarEventParser _parser;
    private readonly TimeZoneService _tzService;
    private readonly ILogger<CalendarEventSkillExecutor> _logger;

    public CalendarEventSkillExecutor(
        IGoogleCalendarEventService calendarService,
        ICalendarEventParser parser,
        TimeZoneService tzService,
        ILogger<CalendarEventSkillExecutor> logger)
    {
        _calendarService = calendarService;
        _parser = parser;
        _tzService = tzService;
        _logger = logger;
    }

    public bool IsConfigured => _calendarService.IsConfigured;

    public async Task<ParsedEventResult?> ParseEventAsync(string text, CancellationToken ct)
    {
        var tz = await _tzService.GetTimeZoneInfoAsync(ct);
        _logger.LogInformation("Calendar event timezone resolved: {TimeZoneId} (baseUtcOffset={Offset})", tz.Id, tz.BaseUtcOffset);

        var parsed = await _parser.ParseAsync(text, tz, ct);
        if (parsed is null)
        {
            return null;
        }

        var preview = BuildEventPreview(parsed, tz);
        return new ParsedEventResult(parsed, preview);
    }

    public async Task<CreateEventResult> CreateEventAsync(ParsedCalendarEvent parsed, CancellationToken ct)
    {
        var result = await _calendarService.CreateEventAsync(parsed, ct);
        return new CreateEventResult(result.Success, result.HtmlLink, result.Error);
    }

    private static string BuildEventPreview(ParsedCalendarEvent ev, TimeZoneInfo tz)
    {
        var localStart = TimeZoneInfo.ConvertTime(ev.StartTime, tz);
        var localEnd = TimeZoneInfo.ConvertTime(ev.StartTime + ev.Duration, tz);

        return $"Create this event?\n\n" +
               $"Title: {ev.Title}\n" +
               $"When: {localStart:ddd MMM d, h:mm tt} - {localEnd:h:mm tt}\n" +
               $"Duration: {FormatDuration(ev.Duration)}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 60)
        {
            return $"{(int)duration.TotalMinutes} min";
        }
        if (duration.TotalHours < 24)
        {
            var hours = (int)duration.TotalHours;
            var mins = duration.Minutes;
            return mins > 0 ? $"{hours}h {mins}min" : $"{hours}h";
        }
        return $"{duration.TotalHours:F1} hours";
    }

    public static string? TryExtractEventText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lowered = text.Trim();

        static string? After(string input, string needle)
        {
            var idx = input.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            return input[(idx + needle.Length)..].Trim();
        }

        var tail = After(lowered, "create event ")
                   ?? After(lowered, "schedule ")
                   ?? After(lowered, "add event ")
                   ?? After(lowered, "new event ")
                   ?? After(lowered, "add ");

        return string.IsNullOrWhiteSpace(tail) ? text : tail;
    }
}
