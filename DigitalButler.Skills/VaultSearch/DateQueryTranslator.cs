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
        // IMPORTANT: Process complex patterns FIRST (most specific to least specific)
        // Then process specific dates BEFORE month names so "January 1st"
        // is recognized as a single day, not the entire month
        ProcessWeekOfYear(query, today, result);      // "last week of 2025"
        ProcessWeekOfMonth(query, today, result);     // "first week of December"
        ProcessWeekend(query, today, result);         // "last weekend", "this weekend"
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
        ProcessSpecificDate(query, today, result);    // Before ProcessMonthName!
        ProcessMonthName(query, today, result);

        return result;
    }

    private static void ProcessWeekOfYear(string query, DateOnly today, TranslatedQuery result)
    {
        // Match "last week of 2025", "first week of 2026"
        var match = WeekOfYearRegex().Match(query);
        if (!match.Success) return;

        var position = match.Groups[1].Value.ToLowerInvariant();
        if (!int.TryParse(match.Groups[2].Value, out var year)) return;

        DateOnly start, end;
        if (position == "last")
        {
            // Last week of the year = last 7 days of December
            end = new DateOnly(year, 12, 31);
            start = end.AddDays(-6);
        }
        else // first
        {
            // First week of the year = first 7 days of January
            start = new DateOnly(year, 1, 1);
            end = start.AddDays(6);
        }

        result.StartDate = start;
        result.EndDate = end;

        for (var d = start; d <= end; d = d.AddDays(1))
        {
            result.DateTerms.Add(d.ToString("yyyy-MM-dd"));
        }
    }

    private static void ProcessWeekOfMonth(string query, DateOnly today, TranslatedQuery result)
    {
        // Match "first week of December", "last week of last December", "first week of this January", "first week of January 2025"
        var match = WeekOfMonthRegex().Match(query);
        if (!match.Success) return;

        var position = match.Groups[1].Value.ToLowerInvariant(); // first/last
        var monthModifier = match.Groups[2].Success ? match.Groups[2].Value.ToLowerInvariant() : null; // "last" or "this" before month name
        var monthName = match.Groups[3].Value;
        var hasYear = match.Groups[4].Success;

        if (!TryParseMonthName(monthName, out var month)) return;

        int year;
        if (hasYear && int.TryParse(match.Groups[4].Value, out var specifiedYear))
        {
            year = specifiedYear;
        }
        else if (monthModifier == "this")
        {
            // "this January" always means January of the current year
            year = today.Year;
        }
        else if (monthModifier == "last")
        {
            // "last December" means December of previous year if we're past December
            year = month >= today.Month ? today.Year - 1 : today.Year;
        }
        else
        {
            // Default: if month is in the future, use last year
            year = month > today.Month ? today.Year - 1 : today.Year;
        }

        var firstOfMonth = new DateOnly(year, month, 1);
        var lastOfMonth = firstOfMonth.AddMonths(1).AddDays(-1);

        DateOnly start, end;
        if (position == "last")
        {
            // Last week of month = last 7 days
            end = lastOfMonth;
            start = end.AddDays(-6);
        }
        else // first
        {
            // First week of month = first 7 days
            start = firstOfMonth;
            end = start.AddDays(6);
        }

        result.StartDate = start;
        result.EndDate = end;

        for (var d = start; d <= end; d = d.AddDays(1))
        {
            result.DateTerms.Add(d.ToString("yyyy-MM-dd"));
        }
    }

    private static void ProcessWeekend(string query, DateOnly today, TranslatedQuery result)
    {
        // Match "last weekend", "this weekend", "next weekend"
        var match = WeekendRegex().Match(query);
        if (!match.Success) return;

        var modifier = match.Groups[1].Value.ToLowerInvariant();

        // Find the Saturday of the relevant weekend
        var currentDayOfWeek = today.DayOfWeek;
        DateOnly saturday;

        if (modifier == "last")
        {
            // Last weekend = previous Saturday-Sunday
            var daysToLastSaturday = (int)currentDayOfWeek + 1; // +1 because Sunday is 0
            if (currentDayOfWeek == DayOfWeek.Sunday)
                daysToLastSaturday = 8; // Go back to previous Saturday
            else if (currentDayOfWeek == DayOfWeek.Saturday)
                daysToLastSaturday = 7; // Go back to previous Saturday
            saturday = today.AddDays(-daysToLastSaturday);
        }
        else if (modifier == "next")
        {
            // Next weekend = upcoming Saturday-Sunday
            var daysToNextSaturday = ((int)DayOfWeek.Saturday - (int)currentDayOfWeek + 7) % 7;
            if (daysToNextSaturday == 0)
                daysToNextSaturday = 7; // If today is Saturday, get next Saturday
            saturday = today.AddDays(daysToNextSaturday);
        }
        else // "this"
        {
            // This weekend = Saturday-Sunday of current week
            var daysToThisSaturday = ((int)DayOfWeek.Saturday - (int)currentDayOfWeek + 7) % 7;
            saturday = today.AddDays(daysToThisSaturday);
        }

        var sunday = saturday.AddDays(1);

        result.StartDate = saturday;
        result.EndDate = sunday;

        result.DateTerms.Add(saturday.ToString("yyyy-MM-dd"));
        result.DateTerms.Add(sunday.ToString("yyyy-MM-dd"));
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

    private static void ProcessSpecificDate(string query, DateOnly today, TranslatedQuery result)
    {
        // Match ISO dates like 2026-01-18
        var isoMatch = IsoDateRegex().Match(query);
        if (isoMatch.Success && DateOnly.TryParse(isoMatch.Value, out var isoDate))
        {
            AddDateTerms(result, isoDate, isoDate);
            return;
        }

        // Match European dates like 01.01.2026 or 01/01/2026 (DD.MM.YYYY or DD/MM/YYYY)
        var europeanMatch = EuropeanDateRegex().Match(query);
        if (europeanMatch.Success)
        {
            if (int.TryParse(europeanMatch.Groups[1].Value, out var day) &&
                int.TryParse(europeanMatch.Groups[2].Value, out var month) &&
                int.TryParse(europeanMatch.Groups[3].Value, out var year))
            {
                if (month >= 1 && month <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
                {
                    var targetDate = new DateOnly(year, month, day);
                    AddDateTerms(result, targetDate, targetDate);
                    return;
                }
            }
        }

        // Match dates like "January 18", "January 18th", "18 January", "18th January"
        var naturalMatch = NaturalDateRegex().Match(query);
        if (naturalMatch.Success)
        {
            // Regex groups: 1=day (day-first), 2=month (day-first), 3=month (month-first), 4=day (month-first)
            string dayStr, monthStr;
            if (naturalMatch.Groups[1].Success)
            {
                // Pattern: "18 January" or "18th January"
                dayStr = naturalMatch.Groups[1].Value;
                monthStr = naturalMatch.Groups[2].Value;
            }
            else
            {
                // Pattern: "January 18" or "January 18th"
                dayStr = naturalMatch.Groups[4].Value;
                monthStr = naturalMatch.Groups[3].Value;
            }

            if (int.TryParse(dayStr, out var day) && TryParseMonthName(monthStr, out var month))
            {
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

    // "first week of 2026", "last week of 2025"
    [GeneratedRegex(@"\b(first|last)\s+week\s+of\s+(\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex WeekOfYearRegex();

    // "first week of December", "last week of last December", "first week of this January", "first week of January 2025"
    [GeneratedRegex(@"\b(first|last)\s+week\s+of\s+(?:(last|this)\s+)?(january|february|march|april|may|june|july|august|september|october|november|december)(?:\s+(\d{4}))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex WeekOfMonthRegex();

    // "last weekend", "this weekend", "next weekend"
    [GeneratedRegex(@"\b(last|this|next)\s+weekend\b", RegexOptions.IgnoreCase)]
    private static partial Regex WeekendRegex();

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

    // European date format: DD.MM.YYYY or DD/MM/YYYY
    [GeneratedRegex(@"\b(\d{1,2})[./](\d{1,2})[./](\d{4})\b")]
    private static partial Regex EuropeanDateRegex();

    // Matches "18 January", "18th January", "January 18", "January 18th", "January 1st", etc.
    [GeneratedRegex(@"\b(?:(\d{1,2})(?:st|nd|rd|th)?\s+(january|february|march|april|may|june|july|august|september|october|november|december)|(january|february|march|april|may|june|july|august|september|october|november|december)\s+(\d{1,2})(?:st|nd|rd|th)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NaturalDateRegex();
}
