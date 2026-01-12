namespace DigitalButler.Skills;

public sealed class GoogleCalendarOAuthOptions
{
    /// <summary>
    /// Path to the service account JSON key file.
    /// </summary>
    public string? ServiceAccountKeyPath { get; set; }

    /// <summary>
    /// Service account JSON content directly (alternative to file path).
    /// Useful for Docker/environment variable configuration.
    /// </summary>
    public string? ServiceAccountJson { get; set; }

    /// <summary>
    /// The calendar ID to add events to.
    /// Use "primary" for the service account's primary calendar,
    /// or use your calendar's email address (e.g., "user@gmail.com").
    /// </summary>
    public string? CalendarId { get; set; }
}
