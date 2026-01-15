using DigitalButler.Common;
using DigitalButler.Skills;

namespace DigitalButler.Telegram.Skills;

public interface ISkillExecutor
{
    ButlerSkill Skill { get; }
    Task<string> ExecuteAsync(CancellationToken ct);
}

public interface ISummarySkillExecutor
{
    Task<string> ExecuteAsync(bool weekly, string taskName, CancellationToken ct);
}

public interface IMotivationSkillExecutor
{
    Task<string> ExecuteAsync(string? userQuery, CancellationToken ct);
}

public interface IActivitiesSkillExecutor
{
    Task<string> ExecuteAsync(CancellationToken ct);
}

public interface IDrawingReferenceSkillExecutor
{
    Task<string> ExecuteAsync(string subject, CancellationToken ct);
    string GetRandomTopic();
}

public interface ICalendarEventSkillExecutor
{
    bool IsConfigured { get; }
    Task<ParsedEventResult?> ParseEventAsync(string text, CancellationToken ct);
    Task<CreateEventResult> CreateEventAsync(ParsedCalendarEvent parsed, CancellationToken ct);
}

public record ParsedEventResult(ParsedCalendarEvent Parsed, string Preview);

public record CreateEventResult(bool Success, string? HtmlLink, string? Error);
