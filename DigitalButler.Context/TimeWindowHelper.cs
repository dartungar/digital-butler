namespace DigitalButler.Context;

/// <summary>
/// Helper for calculating time windows for daily/weekly summaries.
/// Shared between BotService and SchedulerService.
/// </summary>
public static class TimeWindowHelper
{
    public static (DateTimeOffset Start, DateTimeOffset End) GetDailyWindow(TimeZoneInfo tz)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);

        var localStart = new DateTime(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, tz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(localStart.AddDays(1), tz);

        return (new DateTimeOffset(startUtc, TimeSpan.Zero), new DateTimeOffset(endUtc, TimeSpan.Zero));
    }

    public static (DateTimeOffset Start, DateTimeOffset End) GetWeeklyWindow(TimeZoneInfo tz)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);

        var localTodayStart = new DateTime(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, DateTimeKind.Unspecified);

        // Week = Monday..Monday (exclusive), in the configured timezone.
        var diff = ((7 + (int)localNow.DayOfWeek - (int)DayOfWeek.Monday) % 7);
        var localWeekStart = localTodayStart.AddDays(-diff);
        var localWeekEnd = localWeekStart.AddDays(7);

        var weekStartUtc = TimeZoneInfo.ConvertTimeToUtc(localWeekStart, tz);
        var weekEndUtc = TimeZoneInfo.ConvertTimeToUtc(localWeekEnd, tz);

        return (new DateTimeOffset(weekStartUtc, TimeSpan.Zero), new DateTimeOffset(weekEndUtc, TimeSpan.Zero));
    }
}
