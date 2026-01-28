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

    // Thresholds for stats alerts (on a 1-10 scale)
    private static class MetricThresholds
    {
        public const int LowEnergy = 4;
        public const int LowMotivation = 4;
        public const int LowLifeSatisfaction = 4;
        public const int HighStress = 7;
        public const int HighEnergy = 8;
        public const int HighMotivation = 8;
    }

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

        // Get current period (yesterday only) - excludes today's incomplete data
        var yesterdayNote = await _dailyRepo.GetByDateAsync(yesterday, ct);
        if (yesterdayNote == null)
            return null;

        // Get comparison period (day before yesterday)
        var dayBeforeYesterdayNote = await _dailyRepo.GetByDateAsync(dayBeforeYesterday, ct);

        // Build current period result from yesterday's note
        var result = BuildAnalysisResult(new List<ObsidianDailyNote> { yesterdayNote }, yesterday, yesterday, isWeekly: false);

        // Fetch today's note for planned tasks
        var todayNote = await _dailyRepo.GetByDateAsync(today, ct);
        if (todayNote != null)
        {
            result.HasTodayData = true;
            result.TodayPendingTasks = todayNote.PendingTasks ?? new();
            result.TodayStarredTasks = todayNote.StarredTasks ?? new();
            result.TodayAttentionTasks = todayNote.AttentionTasks ?? new();
        }

        // Include yesterday's journal for reflection
        result.YesterdayJournal = yesterdayNote.Notes;

        // Generate stats alerts based on yesterday's metrics
        result.StatsAlerts = GenerateStatsAlerts(yesterdayNote);

        // Calculate comparison vs day before yesterday
        if (dayBeforeYesterdayNote != null)
        {
            result.EnergyDelta = ComputeDelta(result.AvgEnergy, dayBeforeYesterdayNote.Energy);
            result.MotivationDelta = ComputeDelta(result.AvgMotivation, dayBeforeYesterdayNote.Motivation);
            result.StressDelta = ComputeDelta(result.AvgStress, dayBeforeYesterdayNote.Stress);
            result.LifeSatisfactionDelta = ComputeDelta(result.AvgLifeSatisfaction, dayBeforeYesterdayNote.LifeSatisfaction);
            result.ComparisonPeriodLabel = "day before yesterday";
        }
        else
        {
            // Fall back to stored last week summary
            var lastWeekSummary = await GetLastWeekSummaryAsync(today, ct);
            if (lastWeekSummary != null)
            {
                result.EnergyDelta = ComputeDelta(result.AvgEnergy, lastWeekSummary.AvgEnergy);
                result.MotivationDelta = ComputeDelta(result.AvgMotivation, lastWeekSummary.AvgMotivation);
                result.StressDelta = ComputeDelta(result.AvgStress, lastWeekSummary.AvgStress);
                result.LifeSatisfactionDelta = ComputeDelta(result.AvgLifeSatisfaction, lastWeekSummary.AvgLifeSatisfaction);
                result.ComparisonPeriodLabel = "last week avg";
            }
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

        // HEADS UP section - stats alerts (only for daily, placed prominently at top)
        if (!analysis.IsWeekly && analysis.StatsAlerts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("HEADS UP (based on yesterday's stats):");
            foreach (var alert in analysis.StatsAlerts)
                sb.AppendLine($"  - {alert}");
        }

        // TODAY'S PLANNED TASKS section (only for daily)
        if (!analysis.IsWeekly && analysis.HasTodayData &&
            (analysis.TodayStarredTasks.Count > 0 || analysis.TodayAttentionTasks.Count > 0 || analysis.TodayPendingTasks.Count > 0))
        {
            sb.AppendLine();
            sb.AppendLine("TODAY'S PLANNED TASKS (from today's daily note):");

            if (analysis.TodayStarredTasks.Count > 0)
            {
                sb.AppendLine("  Priority [*]:");
                foreach (var task in analysis.TodayStarredTasks.Take(5))
                    sb.AppendLine($"    - {task}");
                if (analysis.TodayStarredTasks.Count > 5)
                    sb.AppendLine($"    ... and {analysis.TodayStarredTasks.Count - 5} more");
            }

            if (analysis.TodayAttentionTasks.Count > 0)
            {
                sb.AppendLine("  Needs attention [!]:");
                foreach (var task in analysis.TodayAttentionTasks.Take(5))
                    sb.AppendLine($"    - {task}");
                if (analysis.TodayAttentionTasks.Count > 5)
                    sb.AppendLine($"    ... and {analysis.TodayAttentionTasks.Count - 5} more");
            }

            if (analysis.TodayPendingTasks.Count > 0)
            {
                sb.AppendLine("  Pending [ ]:");
                foreach (var task in analysis.TodayPendingTasks.Take(10))
                    sb.AppendLine($"    - {task}");
                if (analysis.TodayPendingTasks.Count > 10)
                    sb.AppendLine($"    ... and {analysis.TodayPendingTasks.Count - 10} more");
            }
        }

        // YESTERDAY'S ACCOMPLISHMENTS section (only for daily, for encouragement)
        if (!analysis.IsWeekly && analysis.CompletedTasksList.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("YESTERDAY'S ACCOMPLISHMENTS (for encouragement):");
            foreach (var task in analysis.CompletedTasksList.Take(10))
                sb.AppendLine($"  - {task}");
            if (analysis.CompletedTasksList.Count > 10)
                sb.AppendLine($"  ... and {analysis.CompletedTasksList.Count - 10} more completed!");
        }

        // YESTERDAY'S JOURNAL section (only for daily, for context and tone)
        if (!analysis.IsWeekly && !string.IsNullOrWhiteSpace(analysis.YesterdayJournal))
        {
            sb.AppendLine();
            sb.AppendLine("YESTERDAY'S JOURNAL (for context - note the tone):");
            var journalTruncated = analysis.YesterdayJournal.Length > 1000
                ? analysis.YesterdayJournal[..1000] + "..."
                : analysis.YesterdayJournal;
            sb.AppendLine($"  {journalTruncated}");
        }

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
        sb.AppendLine($"  Completed [x]: {analysis.TotalCompletedTasks}");
        if (analysis.CompletedTasksList.Count > 0)
        {
            foreach (var task in analysis.CompletedTasksList.Take(10))
                sb.AppendLine($"    - {task}");
            if (analysis.CompletedTasksList.Count > 10)
                sb.AppendLine($"    ... and {analysis.CompletedTasksList.Count - 10} more");
        }

        sb.AppendLine($"  Pending [ ]: {analysis.TotalPendingTasks}");
        if (analysis.PendingTasksList.Count > 0)
        {
            foreach (var task in analysis.PendingTasksList.Take(5))
                sb.AppendLine($"    - {task}");
            if (analysis.PendingTasksList.Count > 5)
                sb.AppendLine($"    ... and {analysis.PendingTasksList.Count - 5} more");
        }

        if (analysis.TotalPartiallyCompleteTasks > 0)
        {
            sb.AppendLine($"  Partially complete [/]: {analysis.TotalPartiallyCompleteTasks}");
            foreach (var task in analysis.PartiallyCompleteTasksList.Take(5))
                sb.AppendLine($"    - {task}");
            if (analysis.PartiallyCompleteTasksList.Count > 5)
                sb.AppendLine($"    ... and {analysis.PartiallyCompleteTasksList.Count - 5} more");
        }

        if (analysis.TotalInQuestionTasks > 0)
        {
            sb.AppendLine($"  In question [?]: {analysis.TotalInQuestionTasks}");
            foreach (var task in analysis.InQuestionTasksList.Take(5))
                sb.AppendLine($"    - {task}");
            if (analysis.InQuestionTasksList.Count > 5)
                sb.AppendLine($"    ... and {analysis.InQuestionTasksList.Count - 5} more");
        }

        if (analysis.TotalRescheduledTasks > 0)
        {
            sb.AppendLine($"  Rescheduled [>]: {analysis.TotalRescheduledTasks}");
            foreach (var task in analysis.RescheduledTasksList.Take(5))
                sb.AppendLine($"    - {task}");
            if (analysis.RescheduledTasksList.Count > 5)
                sb.AppendLine($"    ... and {analysis.RescheduledTasksList.Count - 5} more");
        }

        if (analysis.TotalCancelledTasks > 0)
        {
            sb.AppendLine($"  Cancelled [-]: {analysis.TotalCancelledTasks}");
            foreach (var task in analysis.CancelledTasksList.Take(5))
                sb.AppendLine($"    - {task}");
            if (analysis.CancelledTasksList.Count > 5)
                sb.AppendLine($"    ... and {analysis.CancelledTasksList.Count - 5} more");
        }

        if (analysis.TotalStarredTasks > 0)
        {
            sb.AppendLine($"  Starred [*]: {analysis.TotalStarredTasks}");
            foreach (var task in analysis.StarredTasksList.Take(5))
                sb.AppendLine($"    - {task}");
            if (analysis.StarredTasksList.Count > 5)
                sb.AppendLine($"    ... and {analysis.StarredTasksList.Count - 5} more");
        }

        if (analysis.TotalAttentionTasks > 0)
        {
            sb.AppendLine($"  Needs attention [!]: {analysis.TotalAttentionTasks}");
            foreach (var task in analysis.AttentionTasksList.Take(5))
                sb.AppendLine($"    - {task}");
            if (analysis.AttentionTasksList.Count > 5)
                sb.AppendLine($"    ... and {analysis.AttentionTasksList.Count - 5} more");
        }

        if (analysis.TotalInformationTasks > 0)
        {
            sb.AppendLine($"  Information [i]: {analysis.TotalInformationTasks}");
            foreach (var task in analysis.InformationTasksList.Take(5))
                sb.AppendLine($"    - {task}");
            if (analysis.InformationTasksList.Count > 5)
                sb.AppendLine($"    ... and {analysis.InformationTasksList.Count - 5} more");
        }

        if (analysis.TotalIdeaTasks > 0)
        {
            sb.AppendLine($"  Ideas [I]: {analysis.TotalIdeaTasks}");
            foreach (var task in analysis.IdeaTasksList.Take(5))
                sb.AppendLine($"    - {task}");
            if (analysis.IdeaTasksList.Count > 5)
                sb.AppendLine($"    ... and {analysis.IdeaTasksList.Count - 5} more");
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

        // Collect tasks by category
        var completedTasks = notes
            .Where(n => n.CompletedTasks != null)
            .SelectMany(n => n.CompletedTasks!)
            .Distinct()
            .ToList();

        var rescheduledTasks = notes
            .Where(n => n.RescheduledTasks != null)
            .SelectMany(n => n.RescheduledTasks!)
            .Distinct()
            .ToList();

        // For weekly summaries: tasks that were rescheduled but later completed within the same week
        // should count as completed (remove from rescheduled, keep in completed)
        if (isWeekly)
        {
            var completedSet = new HashSet<string>(completedTasks, StringComparer.OrdinalIgnoreCase);
            rescheduledTasks = rescheduledTasks
                .Where(t => !completedSet.Contains(t))
                .ToList();
        }

        result.CompletedTasksList = completedTasks;
        result.TotalCompletedTasks = result.CompletedTasksList.Count;

        result.RescheduledTasksList = rescheduledTasks;
        result.TotalRescheduledTasks = result.RescheduledTasksList.Count;

        result.PendingTasksList = notes
            .Where(n => n.PendingTasks != null)
            .SelectMany(n => n.PendingTasks!)
            .Distinct()
            .ToList();
        result.TotalPendingTasks = result.PendingTasksList.Count;

        result.InQuestionTasksList = notes
            .Where(n => n.InQuestionTasks != null)
            .SelectMany(n => n.InQuestionTasks!)
            .Distinct()
            .ToList();
        result.TotalInQuestionTasks = result.InQuestionTasksList.Count;

        result.PartiallyCompleteTasksList = notes
            .Where(n => n.PartiallyCompleteTasks != null)
            .SelectMany(n => n.PartiallyCompleteTasks!)
            .Distinct()
            .ToList();
        result.TotalPartiallyCompleteTasks = result.PartiallyCompleteTasksList.Count;

        result.CancelledTasksList = notes
            .Where(n => n.CancelledTasks != null)
            .SelectMany(n => n.CancelledTasks!)
            .Distinct()
            .ToList();
        result.TotalCancelledTasks = result.CancelledTasksList.Count;

        result.StarredTasksList = notes
            .Where(n => n.StarredTasks != null)
            .SelectMany(n => n.StarredTasks!)
            .Distinct()
            .ToList();
        result.TotalStarredTasks = result.StarredTasksList.Count;

        result.AttentionTasksList = notes
            .Where(n => n.AttentionTasks != null)
            .SelectMany(n => n.AttentionTasks!)
            .Distinct()
            .ToList();
        result.TotalAttentionTasks = result.AttentionTasksList.Count;

        result.InformationTasksList = notes
            .Where(n => n.InformationTasks != null)
            .SelectMany(n => n.InformationTasks!)
            .Distinct()
            .ToList();
        result.TotalInformationTasks = result.InformationTasksList.Count;

        result.IdeaTasksList = notes
            .Where(n => n.IdeaTasks != null)
            .SelectMany(n => n.IdeaTasks!)
            .Distinct()
            .ToList();
        result.TotalIdeaTasks = result.IdeaTasksList.Count;

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

    private async Task<ObsidianWeeklySummary?> GetLastWeekSummaryAsync(DateOnly today, CancellationToken ct)
    {
        var thisWeekStart = GetMondayOfWeek(today);
        var lastWeekStart = thisWeekStart.AddDays(-7);
        return await _weeklyRepo.GetByWeekStartAsync(lastWeekStart, ct);
    }

    private static List<string> GenerateStatsAlerts(ObsidianDailyNote note)
    {
        var alerts = new List<string>();

        // === METRICS-BASED ALERTS ===

        // Check for low energy/motivation (concerning)
        if (note.Energy.HasValue && note.Energy <= MetricThresholds.LowEnergy)
            alerts.Add($"Yesterday's energy was low ({note.Energy}/10) - consider taking it easy today");

        if (note.Motivation.HasValue && note.Motivation <= MetricThresholds.LowMotivation)
            alerts.Add($"Yesterday's motivation was low ({note.Motivation}/10) - be gentle with yourself");

        if (note.LifeSatisfaction.HasValue && note.LifeSatisfaction <= MetricThresholds.LowLifeSatisfaction)
            alerts.Add($"Yesterday's life satisfaction was low ({note.LifeSatisfaction}/10)");

        // Check for high stress (concerning)
        if (note.Stress.HasValue && note.Stress >= MetricThresholds.HighStress)
            alerts.Add($"Yesterday's stress was elevated ({note.Stress}/10) - prioritize self-care");

        // Check for high energy/motivation (positive)
        if (note.Energy.HasValue && note.Energy >= MetricThresholds.HighEnergy)
            alerts.Add($"Yesterday's energy was high ({note.Energy}/10) - great momentum!");

        if (note.Motivation.HasValue && note.Motivation >= MetricThresholds.HighMotivation)
            alerts.Add($"Yesterday's motivation was high ({note.Motivation}/10) - keep it up!");

        // === HABIT-BASED ALERTS ===

        // Body activities (positive feedback for high activity)
        if (note.BodyCount.HasValue && note.BodyCount >= 3)
            alerts.Add($"Great physical activity yesterday ({note.BodyCount} body activities) - keep moving!");

        // Soul activities (positive feedback)
        if (note.SoulCount.HasValue && note.SoulCount >= 3)
            alerts.Add($"Good soul nourishment yesterday ({note.SoulCount} soul activities)!");

        // Meditation (positive feedback)
        if (note.MeditationMinutes.HasValue && note.MeditationMinutes >= 15)
            alerts.Add($"Nice meditation practice yesterday ({note.MeditationMinutes} min)!");

        // Indulging (gentle suggestion if high)
        if (note.IndulgingCount.HasValue && note.IndulgingCount >= 3)
            alerts.Add($"Noticed some indulging yesterday ({note.IndulgingCount}) - maybe balance it out today?");

        // Areas (life areas maintenance)
        if (note.AreasCount.HasValue && note.AreasCount >= 3)
            alerts.Add($"Good work on life areas yesterday ({note.AreasCount} activities)!");

        return alerts;
    }
}
