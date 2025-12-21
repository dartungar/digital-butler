using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using DigitalButler.Modules.Repositories;

namespace DigitalButler.Modules;

public sealed class GoogleCalendarContextSource : IContextSource
{
    private readonly HttpClient _httpClient;
    private readonly GoogleCalendarOptions _options;
    private readonly ILogger<GoogleCalendarContextSource> _logger;
    private readonly GoogleCalendarFeedRepository _feeds;

    public GoogleCalendarContextSource(HttpClient httpClient, GoogleCalendarFeedRepository feeds, IOptions<GoogleCalendarOptions> options, ILogger<GoogleCalendarContextSource> logger)
    {
        _httpClient = httpClient;
        _feeds = feeds;
        _options = options.Value;
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
                var calendar = Calendar.Load(ics);
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
                        Body = BuildBody(feed.Name, ev, startUtc, occEndUtc),
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

    private static string BuildBody(string feedName, CalendarEvent ev, DateTime startUtc, DateTime? endUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Calendar: {feedName}");
        sb.AppendLine($"Start (UTC): {startUtc:O}");
        if (endUtc is not null)
        {
            sb.AppendLine($"End (UTC): {endUtc:O}");
        }

        if (!string.IsNullOrWhiteSpace(ev.Location))
        {
            sb.AppendLine($"Location: {ev.Location}");
        }

        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            sb.AppendLine();
            sb.AppendLine(Truncate(ev.Description, 4000));
        }

        return sb.ToString().Trim();
    }

    private static string Truncate(string value, int maxLen)
    {
        if (value.Length <= maxLen)
        {
            return value;
        }
        return value[..maxLen];
    }
}
