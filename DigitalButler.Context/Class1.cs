namespace DigitalButler.Context;

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
    Task<string> SummarizeAsync(IEnumerable<ContextItem> items, IReadOnlyDictionary<ContextSource, string> instructionsBySource, string taskName, CancellationToken ct = default);
}

public interface IScheduleService
{
    Task ScheduleUpdateAsync(ContextSource source, string cronOrInterval, CancellationToken ct = default);
    Task ScheduleDailySummaryAsync(TimeOnly time, CancellationToken ct = default);
    Task ScheduleWeeklySummaryAsync(DayOfWeek day, TimeOnly time, CancellationToken ct = default);
}