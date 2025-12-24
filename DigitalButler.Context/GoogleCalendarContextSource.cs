using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DigitalButler.Context.Repositories;

namespace DigitalButler.Context;

public sealed class GoogleCalendarContextSource : IContextSource
{
    private readonly HttpClient _httpClient;
    private readonly GoogleCalendarOptions _options;
    private readonly ILogger<GoogleCalendarContextSource> _logger;
    private readonly GoogleCalendarFeedRepository _feeds;
    private readonly TimeZoneService _timeZones;

    public GoogleCalendarContextSource(HttpClient httpClient, GoogleCalendarFeedRepository feeds, IOptions<GoogleCalendarOptions> options, TimeZoneService timeZones, ILogger<GoogleCalendarContextSource> logger)
    {
        _httpClient = httpClient;
        _feeds = feeds;
        _options = options.Value;
        _timeZones = timeZones;
        _logger = logger;
    }

    public ContextSource Source => ContextSource.GoogleCalendar;

    public async Task<IReadOnlyList<ContextItem>> FetchAsync(CancellationToken ct = default)
    {
        var feeds = await GetFeedsAsync(ct);
        if (feeds.Count == 0)
        {
            _logger.LogInformation("No Google Calendar iCal feeds configured");
            return Array.Empty<ContextItem>();
        }

        var tz = await _timeZones.GetTimeZoneInfoAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var windowStart = now.AddDays(-Math.Abs(_options.DaysBack));
        var windowEnd = now.AddDays(Math.Abs(_options.DaysForward));

        var results = new List<ContextItem>(capacity: 256);

        foreach (var feed in feeds)
        {
            if (string.IsNullOrWhiteSpace(feed.Url))
            {
                continue;
            }

            try
            {
                var ics = await _httpClient.GetStringAsync(feed.Url, ct);
                var calendar = Ical.Net.Calendar.Load(ics);
                if (calendar is null)
                {
                    _logger.LogWarning("Failed to parse iCal feed '{FeedName}' (Calendar.Load returned null)", feed.Name);
                    continue;
                }

                // Ical.Net 5.x exposes GetOccurrences(startTime, options). For unbounded recurrence
                // rules, you must apply TakeWhile/filters to avoid enumerating to "infinity".
                var start = new CalDateTime(windowStart.UtcDateTime, "UTC");
                var windowEndUtc = windowEnd.UtcDateTime;

                var occurrences = calendar
                    .GetOccurrences(start)
                    .TakeWhile(o => (o.Period?.StartTime?.AsUtc ?? DateTime.MaxValue) < windowEndUtc);

                foreach (var occ in occurrences)
                {
                    if (occ.Source is not CalendarEvent ev)
                    {
                        continue;
                    }

                    if (IsCancelled(ev))
                    {
                        continue;
                    }

                    // Unique per occurrence.
                    var period = occ.Period;
                    if (period is null)
                    {
                        continue;
                    }

                    if (period.StartTime is null)
                    {
                        continue;
                    }

                    var startUtc = period.StartTime.AsUtc;
                    var occEndUtc = period.EndTime?.AsUtc;

                    if (startUtc >= windowEndUtc)
                    {
                        continue;
                    }

                    var externalId = BuildExternalId(feed.Name, ev, period.StartTime);

                    results.Add(new ContextItem
                    {
                        Source = ContextSource.GoogleCalendar,
                        ExternalId = externalId,
                        Title = Truncate(ev.Summary ?? "(No title)", 512),
                        Body = BuildBody(feed.Name, ev, startUtc, occEndUtc, tz),
                        RelevantDate = new DateTimeOffset(startUtc, TimeSpan.Zero),
                        IsTimeless = false,
                        Category = "Calendar",
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch/parse iCal feed '{FeedName}'", feed.Name);
            }
        }

        return results;
    }

    private async Task<List<GoogleCalendarIcalFeed>> GetFeedsAsync(CancellationToken ct)
    {
        // Prefer DB-managed feeds (Admin UI), so you can add/remove calendars without redeploying.
        var dbFeeds = await _feeds.GetEnabledAsync(ct);

        if (dbFeeds.Count > 0)
        {
            return dbFeeds
                .Select(x => new GoogleCalendarIcalFeed { Name = x.Name, Url = x.Url })
                .ToList();
        }

        // Fallback: appsettings/env-var configuration.
        return GetConfiguredFeeds().ToList();
    }

    private IEnumerable<GoogleCalendarIcalFeed> GetConfiguredFeeds()
    {
        foreach (var feed in _options.IcalFeeds)
        {
            if (!string.IsNullOrWhiteSpace(feed.Url))
            {
                yield return feed;
            }
        }

        // Optional env-var style: "name|url;name2|url2" or just "url;url2".
        var raw = _options.IcalUrls;
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = "Google Calendar";
            var url = part;

            var pipeIdx = part.IndexOf('|');
            if (pipeIdx > 0)
            {
                name = part[..pipeIdx].Trim();
                url = part[(pipeIdx + 1)..].Trim();
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                yield return new GoogleCalendarIcalFeed { Name = string.IsNullOrWhiteSpace(name) ? "Google Calendar" : name, Url = url };
            }
        }
    }

    private static bool IsCancelled(CalendarEvent ev)
    {
        // RFC5545 STATUS=CANCELLED
        return string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildExternalId(string feedName, CalendarEvent ev, CalDateTime occurrenceStart)
    {
        var uid = string.IsNullOrWhiteSpace(ev.Uid) ? "(no-uid)" : ev.Uid;
        var startKey = occurrenceStart.AsUtc.ToString("yyyyMMdd'T'HHmmss'Z'");
        // Feed name included to avoid collisions across accounts.
        return $"gcal:{feedName}:{uid}:{startKey}";
    }

    private static string BuildBody(string feedName, CalendarEvent ev, DateTime startUtc, DateTime? endUtc, TimeZoneInfo tz)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Calendar: {feedName}");

        var start = new DateTimeOffset(startUtc, TimeSpan.Zero);
        var localStart = TimeZoneInfo.ConvertTime(start, tz);

        sb.AppendLine($"Start: {FormatLocal(localStart)} ({tz.Id})");
        sb.AppendLine($"Start (UTC): {startUtc:O}");
        if (endUtc is not null)
        {
            var end = new DateTimeOffset(endUtc.Value, TimeSpan.Zero);
            var localEnd = TimeZoneInfo.ConvertTime(end, tz);
            sb.AppendLine($"End: {FormatLocal(localEnd)} ({tz.Id})");
            sb.AppendLine($"End (UTC): {endUtc:O}");
        }

        if (!string.IsNullOrWhiteSpace(ev.Location))
        {
            sb.AppendLine($"Location: {ev.Location}");
        }

        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            var (clean, meetLinks) = CleanCalendarDescription(ev.Description);

            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(clean))
            {
                sb.AppendLine(Truncate(clean, 4000));
            }

            if (meetLinks.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    sb.AppendLine();
                }

                foreach (var link in meetLinks)
                {
                    sb.AppendLine($"Google Meet: {link}");
                }
            }
        }

        return sb.ToString().Trim();
    }

    private static readonly Regex MeetLinkRegex = new(
        @"https?://meet\.google\.com/[A-Za-z0-9-]+\b[^\s]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static (string Cleaned, List<string> MeetLinks) CleanCalendarDescription(string description)
    {
        var text = SanitizeFreeText(description);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, new List<string>());
        }

        var meetLinks = new HashSet<string>(StringComparer.Ordinal);

        // Normalize newlines.
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                kept.Add(string.Empty);
                continue;
            }

            // Extract Meet links even if we remove the line.
            foreach (Match m in MeetLinkRegex.Matches(line))
            {
                if (m.Success && !string.IsNullOrWhiteSpace(m.Value))
                {
                    meetLinks.Add(m.Value.Trim());
                }
            }

            // Google sometimes injects localized Meet boilerplate into ICS descriptions.
            // Strip only known template lines; keep user-authored content (including Georgian) intact.
            if (IsGoogleMeetBoilerplateLine(line))
            {
                continue;
            }

            // Also remove the generic support link line.
            if (line.Contains("support.google.com/a/users/answer/9282720", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            kept.Add(line);
        }

        // Collapse excessive blank lines.
        var cleaned = string.Join("\n", kept)
            .Replace("\n\n\n", "\n\n")
            .Trim();

        return (cleaned, meetLinks.OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    private static bool IsGoogleMeetBoilerplateLine(string line)
    {
        // English templates.
        if (line.Contains("Join with Google Meet", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("Learn more about Meet", StringComparison.OrdinalIgnoreCase)) return true;

        // Georgian templates (observed in generated ICS).
        if (line.Contains("შეუერთდით Google Meet", StringComparison.Ordinal)) return true;
        if (line.Contains("შეიტყვეთ მეტი Meet", StringComparison.Ordinal)) return true;

        // If a line is only the Meet link itself, we replace it with our standardized output.
        if (MeetLinkRegex.IsMatch(line) && line.Length <= 200) return true;

        return false;
    }

    private static string Truncate(string value, int maxLen)
    {
        if (value.Length <= maxLen)
        {
            return value;
        }
        return value[..maxLen];
    }

    private static string FormatLocal(DateTimeOffset value)
    {
        // Human-friendly but stable.
        return value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string SanitizeFreeText(string value)
    {
        // Removes control chars and normalizes text to reduce weird artifacts from iCal payloads.
        // Keep all scripts (Cyrillic, etc.) intact; only drop non-printable/control characters.
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (ch == '\n' || ch == '\t')
            {
                sb.Append(ch);
                continue;
            }

            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Surrogate)
            {
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString().Trim();
    }
}
