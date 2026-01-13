namespace DigitalButler.Common;

public enum ContextSource
{
    GoogleCalendar,
    Gmail,
    Personal,
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
    Summary = 0,
    Motivation = 1,
    Activities = 2,
    DrawingReference = 3,
    CalendarEvent = 4
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
            ButlerSkill.Summary => ContextSourceMask.All(),
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
