using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalButler.Skills;

public interface IGoogleCalendarEventService
{
    bool IsConfigured { get; }
    Task<CalendarEventResult> CreateEventAsync(ParsedCalendarEvent ev, CancellationToken ct = default);
}

public readonly record struct CalendarEventResult(bool Success, string? EventId, string? HtmlLink, string? Error);

public sealed class GoogleCalendarEventService : IGoogleCalendarEventService
{
    private readonly GoogleCalendarOAuthOptions _options;
    private readonly ILogger<GoogleCalendarEventService> _logger;
    private readonly GoogleCredential? _credential;

    public GoogleCalendarEventService(
        IOptions<GoogleCalendarOAuthOptions> options,
        ILogger<GoogleCalendarEventService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _credential = LoadCredential();
    }

    public bool IsConfigured => _credential is not null && !string.IsNullOrWhiteSpace(_options.CalendarId);

    private GoogleCredential? LoadCredential()
    {
        try
        {
            // Option 1: JSON content directly in environment variable
            if (!string.IsNullOrWhiteSpace(_options.ServiceAccountJson))
            {
                return GoogleCredential
                    .FromJson(_options.ServiceAccountJson)
                    .CreateScoped(CalendarService.Scope.CalendarEvents);
            }

            // Option 2: Path to JSON file
            if (!string.IsNullOrWhiteSpace(_options.ServiceAccountKeyPath) && File.Exists(_options.ServiceAccountKeyPath))
            {
                using var stream = File.OpenRead(_options.ServiceAccountKeyPath);
                return GoogleCredential
                    .FromStream(stream)
                    .CreateScoped(CalendarService.Scope.CalendarEvents);
            }

            _logger.LogWarning("Google Calendar service account not configured. Set GOOGLE_SERVICE_ACCOUNT_JSON or GOOGLE_SERVICE_ACCOUNT_KEY_PATH");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Google service account credentials");
            return null;
        }
    }

    public async Task<CalendarEventResult> CreateEventAsync(ParsedCalendarEvent ev, CancellationToken ct = default)
    {
        if (_credential is null)
        {
            return new CalendarEventResult(false, null, null, "Google Calendar not configured. Set GOOGLE_SERVICE_ACCOUNT_JSON or GOOGLE_SERVICE_ACCOUNT_KEY_PATH.");
        }

        if (string.IsNullOrWhiteSpace(_options.CalendarId))
        {
            return new CalendarEventResult(false, null, null, "Calendar ID not configured. Set GOOGLE_CALENDAR_ID.");
        }

        try
        {
            var service = new CalendarService(new BaseClientService.Initializer
            {
                HttpClientInitializer = _credential,
                ApplicationName = "Digital Butler"
            });

            var calendarEvent = new Event
            {
                Summary = ev.Title,
                Start = new EventDateTime
                {
                    DateTimeDateTimeOffset = ev.StartTime,
                    TimeZone = ev.TimeZone ?? "UTC"
                },
                End = new EventDateTime
                {
                    DateTimeDateTimeOffset = ev.StartTime + ev.Duration,
                    TimeZone = ev.TimeZone ?? "UTC"
                }
            };

            if (!string.IsNullOrWhiteSpace(ev.Location))
            {
                calendarEvent.Location = ev.Location;
            }

            if (!string.IsNullOrWhiteSpace(ev.Description))
            {
                calendarEvent.Description = ev.Description;
            }

            var request = service.Events.Insert(calendarEvent, _options.CalendarId);
            var createdEvent = await request.ExecuteAsync(ct);

            _logger.LogInformation("Created calendar event {EventId}", createdEvent.Id);
            return new CalendarEventResult(true, createdEvent.Id, createdEvent.HtmlLink, null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogError(ex, "Google Calendar API error");
            return new CalendarEventResult(false, null, null, $"Google Calendar API error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create calendar event");
            return new CalendarEventResult(false, null, null, $"Failed to create event: {ex.Message}");
        }
    }
}
