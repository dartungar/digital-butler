namespace DigitalButler.Common;

public enum ContextSource
{
    GoogleCalendar,
    Gmail,
    Personal,
    Obsidian,
    Other
}

public class ContextItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ContextSource Source { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset? RelevantDate { get; set; }
    public bool IsTimeless { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ExternalId { get; set; }
    public string? Category { get; set; }
    public string? Summary { get; set; }

    // Media metadata: stores transcripts for voice or descriptions for images
    public string? MediaMetadata { get; set; }
    public string? MediaType { get; set; } // "voice", "image"
}

public class Instruction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ContextSource Source { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ScheduleConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ContextSource Source { get; set; }
    public string CronOrInterval { get; set; } = "0 */1 * * *"; // default hourly
    public bool Enabled { get; set; } = true;
}

public class SummarySchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsWeekly { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public TimeOnly Time { get; set; }
    public bool Enabled { get; set; } = true;
}

public enum ButlerSkill
{
    DailySummary = 0,    // Was Summary
    Motivation = 1,
    Activities = 2,
    DrawingReference = 3,
    CalendarEvent = 4,
    WeeklySummary = 5,
    VaultSearch = 6      // Search Obsidian vault
}

public class SkillInstruction
{
    public Guid Id { get; set; }
    public ButlerSkill Skill { get; set; }
    public string Content { get; set; } = string.Empty;
    /// <summary>
    /// Bitmask of allowed <see cref="ContextSource"/> values for this skill.
    /// Use -1 to indicate "use defaults" (backwards-compatible behavior).
    /// </summary>
    public int ContextSourcesMask { get; set; } = -1;

    /// <summary>
    /// When enabled, Butler will generate an extra AI-driven "self thought" snippet
    /// and include it as additional context for this skill.
    /// </summary>
    public bool EnableAiContext { get; set; } = false;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class ContextSourceMask
{
    public static int For(ContextSource source)
        => 1 << (int)source;

    public static int All()
        => Enum.GetValues<ContextSource>().Aggregate(0, (mask, s) => mask | For(s));

    public static bool Contains(int mask, ContextSource source)
        => (mask & For(source)) != 0;
}

public static class SkillContextDefaults
{
    public static int DefaultSourcesMask(ButlerSkill skill)
        => skill switch
        {
            ButlerSkill.DailySummary => ContextSourceMask.All(),
            ButlerSkill.WeeklySummary => ContextSourceMask.All(),
            ButlerSkill.Motivation => ContextSourceMask.For(ContextSource.Personal),
            ButlerSkill.Activities => ContextSourceMask.For(ContextSource.Personal),
            ButlerSkill.CalendarEvent => 0, // No context sources needed for event creation
            _ => ContextSourceMask.All()
        };

    public static int ResolveSourcesMask(ButlerSkill skill, int configuredMask)
        => configuredMask < 0 ? DefaultSourcesMask(skill) : configuredMask;
}

public class AiTaskSetting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TaskName { get; set; } = string.Empty; // e.g. "daily-summary", "weekly-summary", "gmail"
    public string? ProviderUrl { get; set; }
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class GoogleCalendarFeed
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Google Calendar";
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public interface IContextSource
{
    ContextSource Source { get; }
    Task<IReadOnlyList<ContextItem>> FetchAsync(CancellationToken ct = default);
}

public interface IContextUpdater
{
    ContextSource Source { get; }
    Task UpdateAsync(CancellationToken ct = default);
}

public interface ISummarizationService
{
    Task<string> SummarizeAsync(
        IEnumerable<ContextItem> items,
        IReadOnlyDictionary<ContextSource, string> instructionsBySource,
        string taskName,
        string? skillInstructions = null,
        CancellationToken ct = default);
}

public interface IScheduleService
{
    Task ScheduleUpdateAsync(ContextSource source, string cronOrInterval, CancellationToken ct = default);
    Task ScheduleDailySummaryAsync(TimeOnly time, CancellationToken ct = default);
    Task ScheduleWeeklySummaryAsync(DayOfWeek day, TimeOnly time, CancellationToken ct = default);
}

public class GoogleOAuthToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Scope { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ObsidianDailyNote
{
    public DateOnly Date { get; set; }

    // Numeric metrics
    public int? LifeSatisfaction { get; set; }
    public int? SelfEsteem { get; set; }
    public int? Presence { get; set; }
    public int? Energy { get; set; }
    public int? Motivation { get; set; }
    public int? Optimism { get; set; }
    public int? Stress { get; set; }
    public int? Irritability { get; set; }
    public int? Obsession { get; set; }
    public int? OfflineTime { get; set; }
    public int? MeditationMinutes { get; set; }
    public decimal? Weight { get; set; }

    // Habit tracking (count = total emojis, items = list)
    public int? SoulCount { get; set; }
    public List<string>? SoulItems { get; set; }
    public int? BodyCount { get; set; }
    public List<string>? BodyItems { get; set; }
    public int? AreasCount { get; set; }
    public List<string>? AreasItems { get; set; }
    public int? LifeCount { get; set; }
    public List<string>? LifeItems { get; set; }
    public int? IndulgingCount { get; set; }
    public List<string>? IndulgingItems { get; set; }
    public List<string>? WeatherItems { get; set; }

    // Tasks
    public List<string>? CompletedTasks { get; set; }
    public List<string>? PendingTasks { get; set; }

    // Content
    public string? Notes { get; set; }
    public List<string>? Tags { get; set; }

    // Metadata
    public string FilePath { get; set; } = string.Empty;
    public DateTimeOffset? FileModifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class ContextUpdateLog
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int ItemsScanned { get; set; }
    public int ItemsAdded { get; set; }
    public int ItemsUpdated { get; set; }
    public int ItemsUnchanged { get; set; }
    public int DurationMs { get; set; }
    public string? Message { get; set; }
    public string? Details { get; set; }
}

/// <summary>
/// Stored weekly summary generated from Obsidian daily notes.
/// Used for historical analysis and week-over-week comparisons.
/// </summary>
public class ObsidianWeeklySummary
{
    public DateOnly WeekStart { get; set; }  // Monday of the week (PK)

    // Aggregated metrics (averages for the week)
    public decimal? AvgLifeSatisfaction { get; set; }
    public decimal? AvgSelfEsteem { get; set; }
    public decimal? AvgPresence { get; set; }
    public decimal? AvgEnergy { get; set; }
    public decimal? AvgMotivation { get; set; }
    public decimal? AvgOptimism { get; set; }
    public decimal? AvgStress { get; set; }
    public decimal? AvgIrritability { get; set; }
    public decimal? AvgObsession { get; set; }
    public decimal? AvgOfflineTime { get; set; }
    public int? TotalMeditationMinutes { get; set; }

    // Habit totals for the week
    public int? TotalSoulCount { get; set; }
    public int? TotalBodyCount { get; set; }
    public int? TotalAreasCount { get; set; }
    public int? TotalLifeCount { get; set; }
    public int? TotalIndulgingCount { get; set; }

    // Task summary
    public int? TotalCompletedTasks { get; set; }
    public int? TotalPendingTasks { get; set; }

    // Days with data
    public int DaysWithData { get; set; }

    // AI-generated summary text
    public string? Summary { get; set; }

    // Top tags for the week (JSON array)
    public List<string>? TopTags { get; set; }

    // Metadata
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Service for sending error notifications to the user via Telegram.
/// </summary>
public interface ITelegramErrorNotifier
{
    Task NotifyErrorAsync(string context, Exception ex, CancellationToken ct = default);
    Task NotifyErrorAsync(string context, string message, CancellationToken ct = default);
}

/// <summary>
/// Analysis result for daily/weekly summaries with comparison data.
/// </summary>
public class ObsidianAnalysisResult
{
    // Current period data
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public bool IsWeekly { get; set; }

    // Aggregated metrics
    public decimal? AvgEnergy { get; set; }
    public decimal? AvgMotivation { get; set; }
    public decimal? AvgLifeSatisfaction { get; set; }
    public decimal? AvgStress { get; set; }
    public decimal? AvgOptimism { get; set; }

    // Habit totals
    public int TotalSoulCount { get; set; }
    public int TotalBodyCount { get; set; }
    public int TotalAreasCount { get; set; }
    public int TotalIndulgingCount { get; set; }
    public int TotalMeditationMinutes { get; set; }

    // Tasks
    public int TotalCompletedTasks { get; set; }
    public int TotalPendingTasks { get; set; }

    // Content
    public List<string> CompletedTasksList { get; set; } = new();
    public List<string> PendingTasksList { get; set; } = new();
    public List<string> JournalHighlights { get; set; } = new();
    public List<string> TopTags { get; set; } = new();

    // Days with data
    public int DaysWithData { get; set; }

    // Comparison data (deltas vs comparison period)
    public decimal? EnergyDelta { get; set; }
    public decimal? MotivationDelta { get; set; }
    public decimal? StressDelta { get; set; }
    public decimal? LifeSatisfactionDelta { get; set; }

    // Comparison period info
    public string? ComparisonPeriodLabel { get; set; }  // "yesterday", "last week", "last 4 weeks avg"
}
