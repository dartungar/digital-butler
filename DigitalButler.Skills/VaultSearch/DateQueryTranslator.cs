using System.Globalization;
using System.Text.RegularExpressions;

namespace DigitalButler.Skills.VaultSearch;

public interface IDateQueryTranslator
{
    /// <summary>
    /// Translates date references in a query to concrete date formats.
    /// Returns the original query augmented with date-specific terms.
    /// </summary>
    TranslatedQuery TranslateQuery(string query, DateTimeOffset referenceDate);
}

public class TranslatedQuery
{
    /// <summary>
    /// The original query with date references preserved.
    /// </summary>
    public string OriginalQuery { get; set; } = string.Empty;

    /// <summary>
    /// Additional search terms for date-based searches (ISO dates, week numbers, etc.)
    /// </summary>
    public List<string> DateTerms { get; set; } = new();

    /// <summary>
    /// Date range if applicable (for filtering).
    /// </summary>
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    /// <summary>
    /// Combined query with date terms appended.
    /// </summary>
    public string CombinedQuery => DateTerms.Count > 0
        ? $"{OriginalQuery} {string.Join(" ", DateTerms)}"
        : OriginalQuery;
}

public partial class DateQueryTranslator : IDateQueryTranslator
{
    public TranslatedQuery TranslateQuery(string query, DateTimeOffset referenceDate)
    {
        var result = new TranslatedQuery { OriginalQuery = query };
        var today = DateOnly.FromDateTime(referenceDate.Date);

        // Process each date pattern
        ProcessYesterday(query, today, result);
        ProcessToday(query, today, result);
        ProcessLastWeek(query, today, result);
        ProcessThisWeek(query, today, result);
        ProcessLastMonth(query, today, result);
        ProcessThisMonth(query, today, result);
        ProcessDaysAgo(query, today, result);
        ProcessWeeksAgo(query, today, result);
        ProcessMonthsAgo(query, today, result);
        ProcessLastDayOfWeek(query, today, result);
        ProcessMonthName(query, today, result);
        ProcessSpecificDate(query, result);

        return result;
    }

    private static void ProcessYesterday(string query, DateOnly today, TranslatedQuery result)
    {
        if (!YesterdayRegex().IsMatch(query)) return;

        var yesterday = today.AddDays(-1);
        AddDateTerms(result, yesterday, yesterday);
    }

    private static void ProcessToday(string query, DateOnly today, TranslatedQuery result)
    {
        if (!TodayRegex().IsMatch(query)) return;

        AddDateTerms(result, today, today);
    }

    private static void ProcessLastWeek(string query, DateOnly today, TranslatedQuery result)
    {
        if (!LastWeekRegex().IsMatch(query)) return;

        // Last week = 7 days ago through yesterday, aligned to week boundaries
        var lastMonday = today.AddDays(-(int)today.DayOfWeek - 6);
        if (today.DayOfWeek == DayOfWeek.Sunday)
            lastMonday = today.AddDays(-13);

        var lastSunday = lastMonday.AddDays(6);

        result.StartDate = lastMonday;
        result.EndDate = lastSunday;

        // Add week number
        var weekNum = ISOWeek.GetWeekOfYear(lastMonday.ToDateTime(TimeOnly.MinValue));
        result.DateTerms.Add($"{lastMonday.Year}-W{weekNum:D2}");

        // Add date range terms
        for (var d = lastMonday; d <= lastSunday; d = d.AddDays(1))
        {
            result.DateTerms.Add(d.ToString("yyyy-MM-dd"));
        }
    }

    private static void ProcessThisWeek(string query, DateOnly today, TranslatedQuery result)
    {
        if (!ThisWeekRegex().IsMatch(query)) return;

        // This week = Monday through today
        var monday = today.AddDays(-(int)today.DayOfWeek + (today.DayOfWeek == DayOfWeek.Sunday ? -6 : 1));

        result.StartDate = monday;
        result.EndDate = today;

        var weekNum = ISOWeek.GetWeekOfYear(monday.ToDateTime(TimeOnly.MinValue));
        result.DateTerms.Add($"{monday.Year}-W{weekNum:D2}");

        for (var d = monday; d <= today; d = d.AddDays(1))
        {
            result.DateTerms.Add(d.ToString("yyyy-MM-dd"));
        }
    }

    private static void ProcessLastMonth(string query, DateOnly today, TranslatedQuery result)
    {
        if (!LastMonthRegex().IsMatch(query)) return;

        var firstOfLastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var lastOfLastMonth = firstOfLastMonth.AddMonths(1).AddDays(-1);

        result.StartDate = firstOfLastMonth;
        result.EndDate = lastOfLastMonth;

        result.DateTerms.Add(firstOfLastMonth.ToString("yyyy-MM"));
        result.DateTerms.Add(firstOfLastMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture));
    }

    private static void ProcessThisMonth(string query, DateOnly today, TranslatedQuery result)
    {
        if (!ThisMonthRegex().IsMatch(query)) return;

        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);

        result.StartDate = firstOfMonth;
        result.EndDate = today;

        result.DateTerms.Add(firstOfMonth.ToString("yyyy-MM"));
        result.DateTerms.Add(firstOfMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture));
    }

    private static void ProcessDaysAgo(string query, DateOnly today, TranslatedQuery result)
    {
        var match = DaysAgoRegex().Match(query);
        if (!match.Success) return;

        if (int.TryParse(match.Groups[1].Value, out var days))
        {
            var targetDate = today.AddDays(-days);
            AddDateTerms(result, targetDate, targetDate);
        }
    }

    private static void ProcessWeeksAgo(string query, DateOnly today, TranslatedQuery result)
    {
        var match = WeeksAgoRegex().Match(query);
        if (!match.Success) return;

        if (int.TryParse(match.Groups[1].Value, out var weeks))
        {
            var targetMonday = today.AddDays(-(int)today.DayOfWeek - (weeks * 7) + 1);
            if (today.DayOfWeek == DayOfWeek.Sunday)
                targetMonday = today.AddDays(-(weeks * 7) - 6);

            var targetSunday = targetMonday.AddDays(6);

            result.StartDate = targetMonday;
            result.EndDate = targetSunday;

            var weekNum = ISOWeek.GetWeekOfYear(targetMonday.ToDateTime(TimeOnly.MinValue));
            result.DateTerms.Add($"{targetMonday.Year}-W{weekNum:D2}");
        }
    }

    private static void ProcessMonthsAgo(string query, DateOnly today, TranslatedQuery result)
    {
        var match = MonthsAgoRegex().Match(query);
        if (!match.Success) return;

        if (int.TryParse(match.Groups[1].Value, out var months))
        {
            var targetMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-months);
            var lastDay = targetMonth.AddMonths(1).AddDays(-1);

            result.StartDate = targetMonth;
            result.EndDate = lastDay;

            result.DateTerms.Add(targetMonth.ToString("yyyy-MM"));
            result.DateTerms.Add(targetMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture));
        }
    }

    private static void ProcessLastDayOfWeek(string query, DateOnly today, TranslatedQuery result)
    {
        var match = LastDayOfWeekRegex().Match(query);
        if (!match.Success) return;

        var dayName = match.Groups[1].Value;
        if (!Enum.TryParse<DayOfWeek>(dayName, true, out var targetDay)) return;

        // Find the most recent occurrence of that day
        var currentDay = today.DayOfWeek;
        var daysBack = (7 + (int)currentDay - (int)targetDay) % 7;
        if (daysBack == 0) daysBack = 7; // "last Monday" when today is Monday means a week ago

        var targetDate = today.AddDays(-daysBack);
        AddDateTerms(result, targetDate, targetDate);
    }

    private static void ProcessMonthName(string query, DateOnly today, TranslatedQuery result)
    {
        var match = MonthNameRegex().Match(query);
        if (!match.Success) return;

        var monthName = match.Groups[1].Value;
        if (!TryParseMonthName(monthName, out var month)) return;

        // Determine year - if the month is in the future, assume last year
        var year = today.Year;
        if (month > today.Month || (month == today.Month && match.Groups[0].Value.Contains("in", StringComparison.OrdinalIgnoreCase)))
        {
            // "in December" when it's January means last December
            // But "in March" when it's January might mean upcoming March - be conservative and use last year
            if (month > today.Month)
            {
                year--;
            }
        }

        var firstOfMonth = new DateOnly(year, month, 1);
        var lastOfMonth = firstOfMonth.AddMonths(1).AddDays(-1);

        // If it's the current month, only go up to today
        if (year == today.Year && month == today.Month)
        {
            lastOfMonth = today;
        }

        result.StartDate = firstOfMonth;
        result.EndDate = lastOfMonth;

        result.DateTerms.Add(firstOfMonth.ToString("yyyy-MM"));
        result.DateTerms.Add(firstOfMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture));
        result.DateTerms.Add(firstOfMonth.ToString("MMMM", CultureInfo.InvariantCulture));
    }

    private static void ProcessSpecificDate(string query, TranslatedQuery result)
    {
        // Match ISO dates like 2026-01-18
        var isoMatch = IsoDateRegex().Match(query);
        if (isoMatch.Success && DateOnly.TryParse(isoMatch.Value, out var isoDate))
        {
            AddDateTerms(result, isoDate, isoDate);
            return;
        }

        // Match dates like "January 18" or "18 January"
        var naturalMatch = NaturalDateRegex().Match(query);
        if (naturalMatch.Success)
        {
            var dayStr = naturalMatch.Groups[1].Success ? naturalMatch.Groups[1].Value : naturalMatch.Groups[3].Value;
            var monthStr = naturalMatch.Groups[2].Success ? naturalMatch.Groups[2].Value : naturalMatch.Groups[4].Value;

            if (int.TryParse(dayStr, out var day) && TryParseMonthName(monthStr, out var month))
            {
                var today = DateOnly.FromDateTime(DateTime.Today);
                var year = today.Year;

                // If the date is in the future, assume last year
                var targetDate = new DateOnly(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
                if (targetDate > today)
                {
                    year--;
                    targetDate = new DateOnly(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
                }

                AddDateTerms(result, targetDate, targetDate);
            }
        }
    }

    private static void AddDateTerms(TranslatedQuery result, DateOnly start, DateOnly end)
    {
        result.StartDate ??= start;
        result.EndDate ??= end;

        for (var d = start; d <= end; d = d.AddDays(1))
        {
            // ISO format (most common in filenames)
            result.DateTerms.Add(d.ToString("yyyy-MM-dd"));

            // Also add without dashes for some filename formats
            result.DateTerms.Add(d.ToString("yyyyMMdd"));

            // Natural language format
            result.DateTerms.Add(d.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture));
            result.DateTerms.Add(d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture));
        }
    }

    private static bool TryParseMonthName(string name, out int month)
    {
        month = 0;
        var normalizedName = name.Trim().ToLowerInvariant();

        var months = new Dictionary<string, int>
        {
            ["jan"] = 1, ["january"] = 1,
            ["feb"] = 2, ["february"] = 2,
            ["mar"] = 3, ["march"] = 3,
            ["apr"] = 4, ["april"] = 4,
            ["may"] = 5,
            ["jun"] = 6, ["june"] = 6,
            ["jul"] = 7, ["july"] = 7,
            ["aug"] = 8, ["august"] = 8,
            ["sep"] = 9, ["september"] = 9,
            ["oct"] = 10, ["october"] = 10,
            ["nov"] = 11, ["november"] = 11,
            ["dec"] = 12, ["december"] = 12
        };

        foreach (var (key, value) in months)
        {
            if (normalizedName.StartsWith(key))
            {
                month = value;
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\byesterday\b", RegexOptions.IgnoreCase)]
    private static partial Regex YesterdayRegex();

    [GeneratedRegex(@"\btoday\b", RegexOptions.IgnoreCase)]
    private static partial Regex TodayRegex();

    [GeneratedRegex(@"\blast\s+week\b", RegexOptions.IgnoreCase)]
    private static partial Regex LastWeekRegex();

    [GeneratedRegex(@"\bthis\s+week\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThisWeekRegex();

    [GeneratedRegex(@"\blast\s+month\b", RegexOptions.IgnoreCase)]
    private static partial Regex LastMonthRegex();

    [GeneratedRegex(@"\bthis\s+month\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThisMonthRegex();

    [GeneratedRegex(@"\b(\d+)\s+days?\s+ago\b", RegexOptions.IgnoreCase)]
    private static partial Regex DaysAgoRegex();

    [GeneratedRegex(@"\b(\d+)\s+weeks?\s+ago\b", RegexOptions.IgnoreCase)]
    private static partial Regex WeeksAgoRegex();

    [GeneratedRegex(@"\b(\d+)\s+months?\s+ago\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthsAgoRegex();

    [GeneratedRegex(@"\blast\s+(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LastDayOfWeekRegex();

    [GeneratedRegex(@"\b(?:in\s+)?(january|february|march|april|may|june|july|august|september|october|november|december)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthNameRegex();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b")]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"\b(?:(\d{1,2})\s+(january|february|march|april|may|june|july|august|september|october|november|december)|(january|february|march|april|may|june|july|august|september|october|november|december)\s+(\d{1,2}))\b", RegexOptions.IgnoreCase)]
    private static partial Regex NaturalDateRegex();
}
