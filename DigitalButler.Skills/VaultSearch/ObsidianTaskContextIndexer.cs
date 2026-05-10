using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DigitalButler.Common;
using DigitalButler.Data.Repositories;
using Microsoft.Extensions.Options;

namespace DigitalButler.Skills.VaultSearch;

public interface IObsidianTaskContextIndexer
{
    Task<ObsidianTaskIndexingResult> IndexTasksAsync(IEnumerable<string> filePaths, CancellationToken ct = default);
}

public sealed class ObsidianTaskIndexingResult
{
    public int TasksIndexed { get; set; }
    public int TasksRemoved { get; set; }
    public List<string> Errors { get; } = new();
}

public sealed partial class ObsidianTaskContextIndexer : IObsidianTaskContextIndexer
{
    public const string Category = "Vault Tasks";

    private readonly ContextRepository _contextRepo;
    private readonly VaultIndexerOptions _options;

    public ObsidianTaskContextIndexer(ContextRepository contextRepo, IOptions<VaultIndexerOptions> options)
    {
        _contextRepo = contextRepo;
        _options = options.Value;
    }

    public async Task<ObsidianTaskIndexingResult> IndexTasksAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
    {
        var result = new ObsidianTaskIndexingResult();
        var tasks = new List<ObsidianTaskLine>();

        foreach (var filePath in filePaths)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var content = await File.ReadAllTextAsync(filePath, ct);
                var relativePath = GetRelativePath(filePath);
                tasks.AddRange(ObsidianTasksParser.Parse(content, relativePath));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add($"{filePath}: {ex.Message}");
            }
        }

        var contextItems = BuildContextItems(tasks).ToList();
        await _contextRepo.UpsertByExternalIdAsync(contextItems, ct);

        if (result.Errors.Count == 0)
        {
            result.TasksRemoved = await _contextRepo.DeleteMissingExternalIdsAsync(
                ContextSource.Obsidian,
                contextItems.Select(i => i.ExternalId).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!),
                category: Category,
                filterByCategory: true,
                ct: ct);
        }

        result.TasksIndexed = contextItems.Count;
        return result;
    }

    private IEnumerable<ContextItem> BuildContextItems(IEnumerable<ObsidianTaskLine> tasks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks.Where(t => IsAgendaStatus(t.Status)))
        {
            foreach (var dateGroup in task.HappensDates.GroupBy(d => d.Date).OrderBy(g => g.Key))
            {
                if (string.IsNullOrWhiteSpace(task.Text))
                {
                    continue;
                }

                var dedupeKey = $"{dateGroup.Key:yyyy-MM-dd}|{NormalizeForDedupe(task.Text)}";
                if (!seen.Add(dedupeKey))
                {
                    continue;
                }

                var externalId = $"obsidian-task:{dateGroup.Key:yyyyMMdd}:{HashKey(dedupeKey)}";
                var body = BuildBody(task, dateGroup);

                yield return new ContextItem
                {
                    Source = ContextSource.Obsidian,
                    Title = task.Text,
                    Body = body,
                    RelevantDate = NoonUtc(dateGroup.Key),
                    IsTimeless = false,
                    ExternalId = externalId,
                    Category = Category,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
        }
    }

    private string GetRelativePath(string fullPath)
    {
        return Path.GetRelativePath(_options.VaultPath, fullPath).Replace('\\', '/');
    }

    private static string BuildBody(ObsidianTaskLine task, IEnumerable<ObsidianTaskDate> matchingDates)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Task: {task.Text}");
        sb.AppendLine($"Status: {FormatStatus(task.Status)}");
        sb.AppendLine($"Source: {task.RelativePath}:{task.LineNumber}");

        var allDates = task.AllDates
            .OrderBy(d => d.Date)
            .ThenBy(d => d.Kind)
            .Select(d => $"{FormatDateKind(d.Kind)} {d.Date:yyyy-MM-dd}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (allDates.Count > 0)
        {
            sb.AppendLine($"Dates: {string.Join("; ", allDates)}");
        }

        var matching = matchingDates
            .Select(d => FormatDateKind(d.Kind))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (matching.Count > 0)
        {
            sb.AppendLine($"Agenda reason: {string.Join(", ", matching)}");
        }

        if (!string.IsNullOrWhiteSpace(task.Recurrence))
        {
            sb.AppendLine($"Recurrence: {task.Recurrence}");
        }

        return sb.ToString().Trim();
    }

    private static bool IsAgendaStatus(ObsidianTaskStatus status)
    {
        return status is ObsidianTaskStatus.Pending
            or ObsidianTaskStatus.InQuestion
            or ObsidianTaskStatus.PartiallyComplete
            or ObsidianTaskStatus.Starred
            or ObsidianTaskStatus.Attention;
    }

    private static DateTimeOffset NoonUtc(DateOnly date)
    {
        return new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified), TimeSpan.Zero);
    }

    private static string HashKey(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string NormalizeForDedupe(string value)
    {
        return CollapseWhitespaceRegex()
            .Replace(ObsidianTasksParser.StripTaskMetadata(value).ToLowerInvariant(), " ")
            .Trim();
    }

    private static string FormatStatus(ObsidianTaskStatus status)
    {
        return status switch
        {
            ObsidianTaskStatus.Pending => "pending",
            ObsidianTaskStatus.Completed => "completed",
            ObsidianTaskStatus.InQuestion => "in question",
            ObsidianTaskStatus.PartiallyComplete => "partially complete",
            ObsidianTaskStatus.Rescheduled => "rescheduled",
            ObsidianTaskStatus.Cancelled => "cancelled",
            ObsidianTaskStatus.Starred => "starred",
            ObsidianTaskStatus.Attention => "attention",
            ObsidianTaskStatus.Information => "information",
            ObsidianTaskStatus.Idea => "idea",
            _ => status.ToString()
        };
    }

    private static string FormatDateKind(ObsidianTaskDateKind kind)
    {
        return kind switch
        {
            ObsidianTaskDateKind.Due => "due",
            ObsidianTaskDateKind.Scheduled => "scheduled",
            ObsidianTaskDateKind.Start => "start",
            ObsidianTaskDateKind.NoteDate => "note date",
            ObsidianTaskDateKind.Created => "created",
            ObsidianTaskDateKind.Done => "done",
            ObsidianTaskDateKind.Cancelled => "cancelled",
            _ => kind.ToString().ToLowerInvariant()
        };
    }

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex CollapseWhitespaceRegex();
}

internal static partial class ObsidianTasksParser
{
    private static readonly Regex TaskLineRegex = new(
        @"^\s*[-*+]\s+\[(?<marker>[ xX?/>\-*!iI])\]\s+(?<text>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DateRegex = new(
        @"(?<kind>📅|⏳|🛫|➕|✅|❌)\s*(?<date>\d{4}-\d{2}-\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RecurrenceRegex = new(
        @"🔁\s*(?<rule>.*?)(?=\s+(?:📅|⏳|🛫|➕|✅|❌|⏫|🔼|🔽|⏬|🔺)|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PriorityRegex = new(
        @"(?:⏫|🔼|🔽|⏬|🔺)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BlockIdRegex = new(
        @"\s+\^[A-Za-z0-9_-]+\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<ObsidianTaskLine> Parse(string content, string relativePath)
    {
        var tasks = new List<ObsidianTaskLine>();
        var noteDate = TryParseNoteDate(relativePath);
        var inCodeFence = false;
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmedStart = line.TrimStart();

            if (trimmedStart.StartsWith("```", StringComparison.Ordinal) ||
                trimmedStart.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence)
            {
                continue;
            }

            var match = TaskLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var rawText = match.Groups["text"].Value.Trim();
            var allDates = ExtractDates(rawText).ToList();
            var recurrence = ExtractRecurrence(rawText);
            var cleanText = StripTaskMetadata(rawText);

            if (string.IsNullOrWhiteSpace(cleanText))
            {
                continue;
            }

            tasks.Add(new ObsidianTaskLine(
                RelativePath: relativePath,
                LineNumber: i + 1,
                Text: cleanText,
                Status: ParseStatus(match.Groups["marker"].Value),
                AllDates: allDates,
                NoteDate: noteDate,
                Recurrence: recurrence));
        }

        return tasks;
    }

    private static DateOnly? TryParseNoteDate(string relativePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(relativePath);

        if (DateOnly.TryParseExact(fileName, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exactDate))
        {
            return exactDate;
        }

        var match = DateInPathRegex().Match(relativePath);
        if (match.Success &&
            DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var embeddedDate))
        {
            return embeddedDate;
        }

        return null;
    }

    public static string StripTaskMetadata(string text)
    {
        var cleaned = DateRegex.Replace(text, "");
        cleaned = RecurrenceRegex.Replace(cleaned, "");
        cleaned = PriorityRegex.Replace(cleaned, "");
        cleaned = BlockIdRegex.Replace(cleaned, "");
        return CollapseWhitespaceRegex().Replace(cleaned, " ").Trim();
    }

    private static IEnumerable<ObsidianTaskDate> ExtractDates(string text)
    {
        foreach (Match match in DateRegex.Matches(text))
        {
            if (!DateOnly.TryParseExact(
                    match.Groups["date"].Value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                continue;
            }

            if (TryParseKind(match.Groups["kind"].Value, out var kind))
            {
                yield return new ObsidianTaskDate(kind, date);
            }
        }
    }

    private static string? ExtractRecurrence(string text)
    {
        var match = RecurrenceRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var rule = match.Groups["rule"].Value.Trim();
        return string.IsNullOrWhiteSpace(rule) ? null : rule;
    }

    private static bool TryParseKind(string value, out ObsidianTaskDateKind kind)
    {
        kind = value switch
        {
            "📅" => ObsidianTaskDateKind.Due,
            "⏳" => ObsidianTaskDateKind.Scheduled,
            "🛫" => ObsidianTaskDateKind.Start,
            "➕" => ObsidianTaskDateKind.Created,
            "✅" => ObsidianTaskDateKind.Done,
            "❌" => ObsidianTaskDateKind.Cancelled,
            _ => default
        };

        return value is "📅" or "⏳" or "🛫" or "➕" or "✅" or "❌";
    }

    private static ObsidianTaskStatus ParseStatus(string marker)
    {
        return marker switch
        {
            "x" or "X" => ObsidianTaskStatus.Completed,
            " " => ObsidianTaskStatus.Pending,
            "?" => ObsidianTaskStatus.InQuestion,
            "/" => ObsidianTaskStatus.PartiallyComplete,
            ">" => ObsidianTaskStatus.Rescheduled,
            "-" => ObsidianTaskStatus.Cancelled,
            "*" => ObsidianTaskStatus.Starred,
            "!" => ObsidianTaskStatus.Attention,
            "i" => ObsidianTaskStatus.Information,
            "I" => ObsidianTaskStatus.Idea,
            _ => ObsidianTaskStatus.Pending
        };
    }

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex CollapseWhitespaceRegex();

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled)]
    private static partial Regex DateInPathRegex();
}

internal sealed record ObsidianTaskLine(
    string RelativePath,
    int LineNumber,
    string Text,
    ObsidianTaskStatus Status,
    IReadOnlyList<ObsidianTaskDate> AllDates,
    DateOnly? NoteDate,
    string? Recurrence)
{
    public IEnumerable<ObsidianTaskDate> HappensDates
    {
        get
        {
            var explicitHappens = AllDates
                .Where(d => d.Kind is ObsidianTaskDateKind.Due
                    or ObsidianTaskDateKind.Scheduled
                    or ObsidianTaskDateKind.Start)
                .ToList();

            if (explicitHappens.Count > 0)
            {
                return explicitHappens;
            }

            return NoteDate.HasValue
                ? new[] { new ObsidianTaskDate(ObsidianTaskDateKind.NoteDate, NoteDate.Value) }
                : Enumerable.Empty<ObsidianTaskDate>();
        }
    }
}

internal sealed record ObsidianTaskDate(ObsidianTaskDateKind Kind, DateOnly Date);

internal enum ObsidianTaskDateKind
{
    Due,
    Scheduled,
    Start,
    NoteDate,
    Created,
    Done,
    Cancelled
}
