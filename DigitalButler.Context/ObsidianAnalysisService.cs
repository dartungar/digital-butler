using System.Text;
using DigitalButler.Common;
using DigitalButler.Data.Repositories;

namespace DigitalButler.Context;

public interface IObsidianAnalysisService
{
    /// <summary>
    /// Analyzes yesterday+today data, comparing against day before yesterday, this week avg, and last week avg.
    /// </summary>
    Task<ObsidianAnalysisResult?> AnalyzeDailyAsync(TimeZoneInfo tz, CancellationToken ct = default);

    /// <summary>
    /// Analyzes this week's data (Mon-Sun), comparing against last week and last 4 weeks avg.
    /// </summary>
    Task<ObsidianAnalysisResult?> AnalyzeWeeklyAsync(TimeZoneInfo tz, CancellationToken ct = default);

    /// <summary>
    /// Generates a formatted string for inclusion in AI prompt context.
    /// </summary>
    string FormatAnalysisForPrompt(ObsidianAnalysisResult analysis);

    /// <summary>
    /// Creates and stores a weekly summary from the analysis.
    /// </summary>
    Task<ObsidianWeeklySummary> StoreWeeklySummaryAsync(ObsidianAnalysisResult analysis, string? aiSummary, CancellationToken ct = default);
}

public sealed class ObsidianAnalysisService : IObsidianAnalysisService
{
    private readonly ObsidianDailyNotesRepository _dailyRepo;
    private readonly ObsidianWeeklySummaryRepository _weeklyRepo;

    public ObsidianAnalysisService(
        ObsidianDailyNotesRepository dailyRepo,
        ObsidianWeeklySummaryRepository weeklyRepo)
    {
        _dailyRepo = dailyRepo;
        _weeklyRepo = weeklyRepo;
    }

    public async Task<ObsidianAnalysisResult?> AnalyzeDailyAsync(TimeZoneInfo tz, CancellationToken ct = default)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var today = DateOnly.FromDateTime(localNow);
        var yesterday = today.AddDays(-1);
        var dayBeforeYesterday = today.AddDays(-2);

        // Get current period (yesterday + today)
        var currentNotes = await _dailyRepo.GetRangeAsync(yesterday, today, ct);
        if (currentNotes.Count == 0)
            return null;

        // Get comparison periods
        var dayBeforeNote = await _dailyRepo.GetByDateAsync(dayBeforeYesterday, ct);

        // This week (Mon to today)
        var thisWeekStart = GetMondayOfWeek(today);
        var thisWeekNotes = await _dailyRepo.GetRangeAsync(thisWeekStart, today, ct);

        // Last week
        var lastWeekStart = thisWeekStart.AddDays(-7);
        var lastWeekEnd = thisWeekStart.AddDays(-1);
        var lastWeekNotes = await _dailyRepo.GetRangeAsync(lastWeekStart, lastWeekEnd, ct);

        // Build current period result
        var result = BuildAnalysisResult(currentNotes, yesterday, today, isWeekly: false);

        // Calculate comparison vs day before yesterday
        if (dayBeforeNote != null)
        {
            result.EnergyDelta = ComputeDelta(result.AvgEnergy, dayBeforeNote.Energy);
            result.MotivationDelta = ComputeDelta(result.AvgMotivation, dayBeforeNote.Motivation);
            result.StressDelta = ComputeDelta(result.AvgStress, dayBeforeNote.Stress);
            result.LifeSatisfactionDelta = ComputeDelta(result.AvgLifeSatisfaction, dayBeforeNote.LifeSatisfaction);
            result.ComparisonPeriodLabel = "day before yesterday";
        }
        else if (lastWeekNotes.Count > 0)
        {
            // Fall back to last week average
            var lastWeekAvg = BuildAnalysisResult(lastWeekNotes, lastWeekStart, lastWeekEnd, isWeekly: true);
            result.EnergyDelta = ComputeDelta(result.AvgEnergy, lastWeekAvg.AvgEnergy);
            result.MotivationDelta = ComputeDelta(result.AvgMotivation, lastWeekAvg.AvgMotivation);
            result.StressDelta = ComputeDelta(result.AvgStress, lastWeekAvg.AvgStress);
            result.LifeSatisfactionDelta = ComputeDelta(result.AvgLifeSatisfaction, lastWeekAvg.AvgLifeSatisfaction);
            result.ComparisonPeriodLabel = "last week avg";
        }

        return result;
    }

    public async Task<ObsidianAnalysisResult?> AnalyzeWeeklyAsync(TimeZoneInfo tz, CancellationToken ct = default)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var today = DateOnly.FromDateTime(localNow);

        // This week (Mon to Sun, or Mon to today if mid-week)
        var thisWeekStart = GetMondayOfWeek(today);
        var thisWeekEnd = thisWeekStart.AddDays(6); // Sunday
        if (thisWeekEnd > today) thisWeekEnd = today;

        var thisWeekNotes = await _dailyRepo.GetRangeAsync(thisWeekStart, thisWeekEnd, ct);
        if (thisWeekNotes.Count == 0)
            return null;

        // Last week
        var lastWeekStart = thisWeekStart.AddDays(-7);
        var lastWeekEnd = thisWeekStart.AddDays(-1);
        var lastWeekNotes = await _dailyRepo.GetRangeAsync(lastWeekStart, lastWeekEnd, ct);

        // Last 4 weeks (for broader comparison)
        var fourWeeksAgoStart = thisWeekStart.AddDays(-28);
        var fourWeeksAgoEnd = lastWeekEnd;
        var last4WeeksNotes = await _dailyRepo.GetRangeAsync(fourWeeksAgoStart, fourWeeksAgoEnd, ct);

        // Build current week result
        var result = BuildAnalysisResult(thisWeekNotes, thisWeekStart, thisWeekEnd, isWeekly: true);

        // Primary comparison: last week
        if (lastWeekNotes.Count > 0)
        {
            var lastWeekResult = BuildAnalysisResult(lastWeekNotes, lastWeekStart, lastWeekEnd, isWeekly: true);
            result.EnergyDelta = ComputeDelta(result.AvgEnergy, lastWeekResult.AvgEnergy);
            result.MotivationDelta = ComputeDelta(result.AvgMotivation, lastWeekResult.AvgMotivation);
            result.StressDelta = ComputeDelta(result.AvgStress, lastWeekResult.AvgStress);
            result.LifeSatisfactionDelta = ComputeDelta(result.AvgLifeSatisfaction, lastWeekResult.AvgLifeSatisfaction);
            result.ComparisonPeriodLabel = "last week";
        }
        else if (last4WeeksNotes.Count > 0)
        {
            // Fall back to 4-week average
            var last4WeeksResult = BuildAnalysisResult(last4WeeksNotes, fourWeeksAgoStart, fourWeeksAgoEnd, isWeekly: true);
            result.EnergyDelta = ComputeDelta(result.AvgEnergy, last4WeeksResult.AvgEnergy);
            result.MotivationDelta = ComputeDelta(result.AvgMotivation, last4WeeksResult.AvgMotivation);
            result.StressDelta = ComputeDelta(result.AvgStress, last4WeeksResult.AvgStress);
            result.LifeSatisfactionDelta = ComputeDelta(result.AvgLifeSatisfaction, last4WeeksResult.AvgLifeSatisfaction);
            result.ComparisonPeriodLabel = "last 4 weeks avg";
        }

        return result;
    }

    public string FormatAnalysisForPrompt(ObsidianAnalysisResult analysis)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"=== Obsidian Daily Notes Analysis ({analysis.PeriodStart:MMM dd} - {analysis.PeriodEnd:MMM dd}) ===");
        sb.AppendLine($"Days with data: {analysis.DaysWithData}");
        sb.AppendLine();

        // Metrics with deltas
        sb.AppendLine("METRICS:");
        if (analysis.AvgEnergy.HasValue)
            sb.AppendLine($"  Energy: {analysis.AvgEnergy:F1}{FormatDelta(analysis.EnergyDelta)}");
        if (analysis.AvgMotivation.HasValue)
            sb.AppendLine($"  Motivation: {analysis.AvgMotivation:F1}{FormatDelta(analysis.MotivationDelta)}");
        if (analysis.AvgLifeSatisfaction.HasValue)
            sb.AppendLine($"  Life Satisfaction: {analysis.AvgLifeSatisfaction:F1}{FormatDelta(analysis.LifeSatisfactionDelta)}");
        if (analysis.AvgStress.HasValue)
            sb.AppendLine($"  Stress: {analysis.AvgStress:F1}{FormatDelta(analysis.StressDelta, invertGood: true)}");
        if (analysis.AvgOptimism.HasValue)
            sb.AppendLine($"  Optimism: {analysis.AvgOptimism:F1}");

        if (!string.IsNullOrEmpty(analysis.ComparisonPeriodLabel))
            sb.AppendLine($"  (compared to {analysis.ComparisonPeriodLabel})");

        sb.AppendLine();

        // Habits
        sb.AppendLine("HABITS:");
        if (analysis.TotalSoulCount > 0) sb.AppendLine($"  Soul activities: {analysis.TotalSoulCount}");
        if (analysis.TotalBodyCount > 0) sb.AppendLine($"  Body activities: {analysis.TotalBodyCount}");
        if (analysis.TotalAreasCount > 0) sb.AppendLine($"  Life areas: {analysis.TotalAreasCount}");
        if (analysis.TotalIndulgingCount > 0) sb.AppendLine($"  Indulging: {analysis.TotalIndulgingCount}");
        if (analysis.TotalMeditationMinutes > 0) sb.AppendLine($"  Meditation: {analysis.TotalMeditationMinutes} min");

        sb.AppendLine();

        // Tasks
        sb.AppendLine("TASKS:");
        sb.AppendLine($"  Completed: {analysis.TotalCompletedTasks}");
        if (analysis.CompletedTasksList.Count > 0)
        {
            foreach (var task in analysis.CompletedTasksList.Take(10))
                sb.AppendLine($"    - {task}");
            if (analysis.CompletedTasksList.Count > 10)
                sb.AppendLine($"    ... and {analysis.CompletedTasksList.Count - 10} more");
        }
        sb.AppendLine($"  Pending: {analysis.TotalPendingTasks}");
        if (analysis.PendingTasksList.Count > 0)
        {
            foreach (var task in analysis.PendingTasksList.Take(5))
                sb.AppendLine($"    - {task}");
            if (analysis.PendingTasksList.Count > 5)
                sb.AppendLine($"    ... and {analysis.PendingTasksList.Count - 5} more");
        }

        // Tags
        if (analysis.TopTags.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"TOP TAGS: {string.Join(", ", analysis.TopTags.Take(10))}");
        }

        // Journal highlights
        if (analysis.JournalHighlights.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("JOURNAL HIGHLIGHTS:");
            foreach (var highlight in analysis.JournalHighlights.Take(5))
            {
                var truncated = highlight.Length > 200 ? highlight[..200] + "..." : highlight;
                sb.AppendLine($"  - {truncated}");
            }
        }

        return sb.ToString();
    }

    public async Task<ObsidianWeeklySummary> StoreWeeklySummaryAsync(ObsidianAnalysisResult analysis, string? aiSummary, CancellationToken ct = default)
    {
        var summary = new ObsidianWeeklySummary
        {
            WeekStart = analysis.PeriodStart,
            AvgEnergy = analysis.AvgEnergy,
            AvgMotivation = analysis.AvgMotivation,
            AvgLifeSatisfaction = analysis.AvgLifeSatisfaction,
            AvgStress = analysis.AvgStress,
            AvgOptimism = analysis.AvgOptimism,
            TotalSoulCount = analysis.TotalSoulCount,
            TotalBodyCount = analysis.TotalBodyCount,
            TotalAreasCount = analysis.TotalAreasCount,
            TotalIndulgingCount = analysis.TotalIndulgingCount,
            TotalMeditationMinutes = analysis.TotalMeditationMinutes,
            TotalCompletedTasks = analysis.TotalCompletedTasks,
            TotalPendingTasks = analysis.TotalPendingTasks,
            DaysWithData = analysis.DaysWithData,
            TopTags = analysis.TopTags.Count > 0 ? analysis.TopTags : null,
            Summary = aiSummary
        };

        await _weeklyRepo.UpsertAsync(summary, ct);
        return summary;
    }

    private static ObsidianAnalysisResult BuildAnalysisResult(
        List<ObsidianDailyNote> notes,
        DateOnly periodStart,
        DateOnly periodEnd,
        bool isWeekly)
    {
        var result = new ObsidianAnalysisResult
        {
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            IsWeekly = isWeekly,
            DaysWithData = notes.Count
        };

        if (notes.Count == 0)
            return result;

        // Compute averages for metrics
        var energyValues = notes.Where(n => n.Energy.HasValue).Select(n => n.Energy!.Value).ToList();
        var motivationValues = notes.Where(n => n.Motivation.HasValue).Select(n => n.Motivation!.Value).ToList();
        var lifeSatValues = notes.Where(n => n.LifeSatisfaction.HasValue).Select(n => n.LifeSatisfaction!.Value).ToList();
        var stressValues = notes.Where(n => n.Stress.HasValue).Select(n => n.Stress!.Value).ToList();
        var optimismValues = notes.Where(n => n.Optimism.HasValue).Select(n => n.Optimism!.Value).ToList();

        if (energyValues.Count > 0) result.AvgEnergy = (decimal)energyValues.Average();
        if (motivationValues.Count > 0) result.AvgMotivation = (decimal)motivationValues.Average();
        if (lifeSatValues.Count > 0) result.AvgLifeSatisfaction = (decimal)lifeSatValues.Average();
        if (stressValues.Count > 0) result.AvgStress = (decimal)stressValues.Average();
        if (optimismValues.Count > 0) result.AvgOptimism = (decimal)optimismValues.Average();

        // Sum habit counts
        result.TotalSoulCount = notes.Sum(n => n.SoulCount ?? 0);
        result.TotalBodyCount = notes.Sum(n => n.BodyCount ?? 0);
        result.TotalAreasCount = notes.Sum(n => n.AreasCount ?? 0);
        result.TotalIndulgingCount = notes.Sum(n => n.IndulgingCount ?? 0);
        result.TotalMeditationMinutes = notes.Sum(n => n.MeditationMinutes ?? 0);

        // Collect tasks
        result.CompletedTasksList = notes
            .Where(n => n.CompletedTasks != null)
            .SelectMany(n => n.CompletedTasks!)
            .Distinct()
            .ToList();
        result.TotalCompletedTasks = result.CompletedTasksList.Count;

        result.PendingTasksList = notes
            .Where(n => n.PendingTasks != null)
            .SelectMany(n => n.PendingTasks!)
            .Distinct()
            .ToList();
        result.TotalPendingTasks = result.PendingTasksList.Count;

        // Collect tags and count occurrences
        var tagCounts = notes
            .Where(n => n.Tags != null)
            .SelectMany(n => n.Tags!)
            .GroupBy(t => t)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();
        result.TopTags = tagCounts;

        // Extract journal highlights (first line of each day's notes)
        result.JournalHighlights = notes
            .Where(n => !string.IsNullOrWhiteSpace(n.Notes))
            .OrderByDescending(n => n.Date)
            .Select(n => ExtractFirstMeaningfulLine(n.Notes!))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(10)
            .ToList()!;

        return result;
    }

    private static string? ExtractFirstMeaningfulLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            // Skip bullet markers only
            var cleaned = line.TrimStart('-', '*', ' ');
            if (cleaned.Length > 10)
                return cleaned;
        }
        return null;
    }

    private static decimal? ComputeDelta(decimal? current, int? previous)
    {
        if (!current.HasValue || !previous.HasValue)
            return null;
        return current.Value - previous.Value;
    }

    private static decimal? ComputeDelta(decimal? current, decimal? previous)
    {
        if (!current.HasValue || !previous.HasValue)
            return null;
        return current.Value - previous.Value;
    }

    private static string FormatDelta(decimal? delta, bool invertGood = false)
    {
        if (!delta.HasValue || Math.Abs(delta.Value) < 0.1m)
            return "";

        var sign = delta.Value > 0 ? "+" : "";
        var indicator = delta.Value > 0
            ? (invertGood ? " [worse]" : " [better]")
            : (invertGood ? " [better]" : " [worse]");

        return $" ({sign}{delta.Value:F1}{indicator})";
    }

    private static DateOnly GetMondayOfWeek(DateOnly date)
    {
        var daysFromMonday = ((int)date.DayOfWeek - 1 + 7) % 7;
        return date.AddDays(-daysFromMonday);
    }
}
