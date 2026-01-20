using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DigitalButler.Common;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DigitalButler.Context;

public static class ObsidianDailyNotesParser
{
    private static readonly Regex FrontmatterRegex = new(
        @"^---\s*\n(.*?)\n---\s*\n",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Matches various checkbox types: [ ] pending, [x]/[X] completed, [?] in question,
    // [/] partially complete, [>] rescheduled, [-] cancelled, [*] starred,
    // [!] attention, [i] information, [I] idea
    private static readonly Regex CheckboxRegex = new(
        @"^-\s*\[([ xX?/>\-\*!iI])\]\s*(.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex InlineTagRegex = new(
        @"#([a-zA-Z0-9_/-]+)",
        RegexOptions.Compiled);

    private static readonly Regex TasksCodeBlockRegex = new(
        @"```tasks[\s\S]*?```",
        RegexOptions.Compiled);

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static ObsidianDailyNote Parse(string filePath)
    {
        var content = File.ReadAllText(filePath, Encoding.UTF8);
        var fileInfo = new FileInfo(filePath);
        var fileName = Path.GetFileNameWithoutExtension(filePath);

        var note = new ObsidianDailyNote
        {
            FilePath = filePath,
            FileModifiedAt = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero)
        };

        // Extract frontmatter
        var frontmatterMatch = FrontmatterRegex.Match(content);
        string bodyContent;

        if (frontmatterMatch.Success)
        {
            var yamlContent = frontmatterMatch.Groups[1].Value;
            ParseFrontmatter(note, yamlContent, fileName);
            bodyContent = content[(frontmatterMatch.Index + frontmatterMatch.Length)..];
        }
        else
        {
            // No frontmatter, try to parse date from filename
            if (TryParseDateFromFilename(fileName, out var date))
            {
                note.Date = date;
            }
            bodyContent = content;
        }

        // Parse body content
        ParseBody(note, bodyContent);

        return note;
    }

    private static void ParseFrontmatter(ObsidianDailyNote note, string yaml, string fileName)
    {
        try
        {
            var data = YamlDeserializer.Deserialize<Dictionary<string, object?>>(yaml);
            if (data is null)
            {
                if (TryParseDateFromFilename(fileName, out var fallbackDate))
                {
                    note.Date = fallbackDate;
                }
                return;
            }

            // Date: prefer journal-date, fallback to filename
            if (data.TryGetValue("journal-date", out var journalDate) && journalDate is string dateStr)
            {
                if (DateOnly.TryParse(dateStr, out var parsedDate))
                {
                    note.Date = parsedDate;
                }
            }

            if (note.Date == default && TryParseDateFromFilename(fileName, out var fileDate))
            {
                note.Date = fileDate;
            }

            // Numeric metrics
            note.LifeSatisfaction = GetInt(data, "life-satisfaction") ?? GetInt(data, "life_satisfaction");
            note.SelfEsteem = GetInt(data, "self-esteem") ?? GetInt(data, "self_esteem");
            note.Presence = GetInt(data, "presence");
            note.Energy = GetInt(data, "energy");
            note.Motivation = GetInt(data, "motivation");
            note.Optimism = GetInt(data, "optimism");
            note.Stress = GetInt(data, "stress");
            note.Irritability = GetInt(data, "irritability");
            note.Obsession = GetInt(data, "obsession");
            note.OfflineTime = GetInt(data, "offline-time") ?? GetInt(data, "offline_time");
            note.MeditationMinutes = GetInt(data, "meditation-minutes") ?? GetInt(data, "meditation_minutes");
            note.Weight = GetDecimal(data, "weight");

            // Array fields with emoji counting
            (note.SoulCount, note.SoulItems) = GetArrayWithEmojiCount(data, "soul");
            (note.BodyCount, note.BodyItems) = GetArrayWithEmojiCount(data, "body");
            (note.AreasCount, note.AreasItems) = GetArrayWithEmojiCount(data, "areas");
            (note.LifeCount, note.LifeItems) = GetArrayWithEmojiCount(data, "life");
            (note.IndulgingCount, note.IndulgingItems) = GetArrayWithEmojiCount(data, "indulging");

            // Weather: items only, no count
            note.WeatherItems = GetStringList(data, "weather");

            // Tags from frontmatter
            var frontmatterTags = GetStringList(data, "tags");
            if (frontmatterTags is not null)
            {
                note.Tags = frontmatterTags
                    .Select(NormalizeTag)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct()
                    .ToList();
            }
        }
        catch
        {
            // If YAML parsing fails, try to at least get the date from filename
            if (TryParseDateFromFilename(fileName, out var fallbackDate))
            {
                note.Date = fallbackDate;
            }
        }
    }

    private static void ParseBody(ObsidianDailyNote note, string body)
    {
        // Remove tasks code blocks (Obsidian Tasks plugin queries)
        var cleanedBody = TasksCodeBlockRegex.Replace(body, "");

        // Extract tasks by status
        var completedTasks = new List<string>();
        var pendingTasks = new List<string>();
        var inQuestionTasks = new List<string>();
        var partiallyCompleteTasks = new List<string>();
        var rescheduledTasks = new List<string>();
        var cancelledTasks = new List<string>();
        var starredTasks = new List<string>();
        var attentionTasks = new List<string>();
        var informationTasks = new List<string>();
        var ideaTasks = new List<string>();

        foreach (Match match in CheckboxRegex.Matches(cleanedBody))
        {
            var marker = match.Groups[1].Value;
            var taskText = match.Groups[2].Value.Trim();

            if (string.IsNullOrWhiteSpace(taskText))
                continue;

            var status = ParseTaskStatus(marker);
            switch (status)
            {
                case ObsidianTaskStatus.Completed:
                    completedTasks.Add(taskText);
                    break;
                case ObsidianTaskStatus.Pending:
                    pendingTasks.Add(taskText);
                    break;
                case ObsidianTaskStatus.InQuestion:
                    inQuestionTasks.Add(taskText);
                    break;
                case ObsidianTaskStatus.PartiallyComplete:
                    partiallyCompleteTasks.Add(taskText);
                    break;
                case ObsidianTaskStatus.Rescheduled:
                    rescheduledTasks.Add(taskText);
                    break;
                case ObsidianTaskStatus.Cancelled:
                    cancelledTasks.Add(taskText);
                    break;
                case ObsidianTaskStatus.Starred:
                    starredTasks.Add(taskText);
                    break;
                case ObsidianTaskStatus.Attention:
                    attentionTasks.Add(taskText);
                    break;
                case ObsidianTaskStatus.Information:
                    informationTasks.Add(taskText);
                    break;
                case ObsidianTaskStatus.Idea:
                    ideaTasks.Add(taskText);
                    break;
            }
        }

        note.CompletedTasks = completedTasks.Count > 0 ? completedTasks : null;
        note.PendingTasks = pendingTasks.Count > 0 ? pendingTasks : null;
        note.InQuestionTasks = inQuestionTasks.Count > 0 ? inQuestionTasks : null;
        note.PartiallyCompleteTasks = partiallyCompleteTasks.Count > 0 ? partiallyCompleteTasks : null;
        note.RescheduledTasks = rescheduledTasks.Count > 0 ? rescheduledTasks : null;
        note.CancelledTasks = cancelledTasks.Count > 0 ? cancelledTasks : null;
        note.StarredTasks = starredTasks.Count > 0 ? starredTasks : null;
        note.AttentionTasks = attentionTasks.Count > 0 ? attentionTasks : null;
        note.InformationTasks = informationTasks.Count > 0 ? informationTasks : null;
        note.IdeaTasks = ideaTasks.Count > 0 ? ideaTasks : null;

        // Extract journal section
        var journalContent = ExtractJournalSection(cleanedBody);
        if (!string.IsNullOrWhiteSpace(journalContent))
        {
            // Extract inline tags from journal
            var inlineTags = InlineTagRegex.Matches(journalContent)
                .Select(m => NormalizeTag(m.Groups[1].Value))
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

            // Merge with frontmatter tags
            if (inlineTags.Count > 0)
            {
                var allTags = (note.Tags ?? new List<string>())
                    .Concat(inlineTags)
                    .Distinct()
                    .ToList();
                note.Tags = allTags.Count > 0 ? allTags : null;
            }

            // Remove inline tags from journal text for cleaner storage
            var cleanJournal = InlineTagRegex.Replace(journalContent, "").Trim();

            // Truncate if needed
            if (cleanJournal.Length > 8000)
            {
                cleanJournal = cleanJournal[..8000];
            }

            note.Notes = string.IsNullOrWhiteSpace(cleanJournal) ? null : cleanJournal;
        }
    }

    private static string? ExtractJournalSection(string body)
    {
        // Look for # Journal heading (case insensitive)
        var journalHeadingRegex = new Regex(@"^#\s*Journal\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var match = journalHeadingRegex.Match(body);

        if (!match.Success)
        {
            return null;
        }

        var startIndex = match.Index + match.Length;

        // Find the next heading (any level)
        var nextHeadingRegex = new Regex(@"^#+\s+", RegexOptions.Multiline);
        var nextHeading = nextHeadingRegex.Match(body, startIndex);

        var endIndex = nextHeading.Success ? nextHeading.Index : body.Length;
        var section = body[startIndex..endIndex].Trim();

        return string.IsNullOrWhiteSpace(section) ? null : section;
    }

    public static bool TryParseDateFromFilename(string fileNameOrPath, out DateOnly date)
    {
        var fileName = Path.GetFileNameWithoutExtension(fileNameOrPath);

        // Try YYYY-MM-DD format
        if (DateOnly.TryParseExact(fileName, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        // Try to extract date pattern from filename
        var datePattern = new Regex(@"(\d{4}-\d{2}-\d{2})");
        var match = datePattern.Match(fileName);
        if (match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        date = default;
        return false;
    }

    private static int? GetInt(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    private static decimal? GetDecimal(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int i => i,
            long l => l,
            double d => (decimal)d,
            decimal dec => dec,
            string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static List<string>? GetStringList(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is List<object> objList)
        {
            var result = objList
                .Select(o => o?.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Cast<string>()
                .ToList();
            return result.Count > 0 ? result : null;
        }

        if (value is string singleValue && !string.IsNullOrWhiteSpace(singleValue))
        {
            return new List<string> { singleValue };
        }

        return null;
    }

    private static (int? Count, List<string>? Items) GetArrayWithEmojiCount(Dictionary<string, object?> data, string key)
    {
        var items = GetStringList(data, key);
        if (items is null || items.Count == 0)
            return (null, null);

        var totalCount = items.Sum(CountLeadingEmojis);
        return (totalCount > 0 ? totalCount : null, items);
    }

    private static int CountLeadingEmojis(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();

            // Check if this text element is an emoji
            if (IsEmoji(element))
            {
                count++;
            }
            else
            {
                // Stop counting once we hit a non-emoji character
                break;
            }
        }

        return count;
    }

    private static bool IsEmoji(string textElement)
    {
        if (string.IsNullOrEmpty(textElement))
            return false;

        // Get the first code point
        var codePoint = char.ConvertToUtf32(textElement, 0);

        // Common emoji ranges
        // Emoticons
        if (codePoint >= 0x1F600 && codePoint <= 0x1F64F) return true;
        // Misc symbols and pictographs
        if (codePoint >= 0x1F300 && codePoint <= 0x1F5FF) return true;
        // Transport and map symbols
        if (codePoint >= 0x1F680 && codePoint <= 0x1F6FF) return true;
        // Supplemental symbols and pictographs
        if (codePoint >= 0x1F900 && codePoint <= 0x1F9FF) return true;
        // Symbols and pictographs extended-A
        if (codePoint >= 0x1FA00 && codePoint <= 0x1FA6F) return true;
        // Symbols and pictographs extended-B
        if (codePoint >= 0x1FA70 && codePoint <= 0x1FAFF) return true;
        // Dingbats
        if (codePoint >= 0x2700 && codePoint <= 0x27BF) return true;
        // Misc symbols
        if (codePoint >= 0x2600 && codePoint <= 0x26FF) return true;
        // Regional indicator symbols (flags)
        if (codePoint >= 0x1F1E0 && codePoint <= 0x1F1FF) return true;

        return false;
    }

    private static string NormalizeTag(string tag)
    {
        // Remove common prefixes like "journal/"
        var normalized = tag.ToLowerInvariant();
        if (normalized.StartsWith("journal/"))
        {
            normalized = normalized["journal/".Length..];
        }
        return normalized;
    }

    private static ObsidianTaskStatus ParseTaskStatus(string marker)
    {
        // Note: case-sensitive for [i] (information) vs [I] (idea)
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
            _ => ObsidianTaskStatus.Pending // Default to pending for unknown markers
        };
    }
}
