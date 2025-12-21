namespace DigitalButler.Modules;

public sealed class GoogleCalendarOptions
{
    /// <summary>
    /// List of iCal feeds to ingest. This supports multiple Google accounts/calendars
    /// by adding multiple feed URLs.
    /// </summary>
    public List<GoogleCalendarIcalFeed> IcalFeeds { get; set; } = new();

    /// <summary>
    /// Also supports a simple env-var format: GCAL_ICAL_URLS="name|url;name2|url2".
    /// If both are provided, this is appended.
    /// </summary>
    public string? IcalUrls { get; set; }

    public int DaysBack { get; set; } = 7;
    public int DaysForward { get; set; } = 90;
}

public sealed class GoogleCalendarIcalFeed
{
    public string Name { get; set; } = "Google Calendar";
    public string Url { get; set; } = string.Empty;
}
