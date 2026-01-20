using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using DigitalButler.Common;

namespace DigitalButler.Skills.VaultSearch;

public interface INoteChunker
{
    IEnumerable<ChunkInfo> ChunkNote(string content, string filePath, string? title);
}

public partial class NoteChunker : INoteChunker
{
    private readonly VaultIndexerOptions _options;

    // Approximate: 1 token ~= 4 characters for English text
    private const int CharsPerToken = 4;

    public NoteChunker(IOptions<VaultIndexerOptions> options)
    {
        _options = options.Value;
    }

    public IEnumerable<ChunkInfo> ChunkNote(string content, string filePath, string? title)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        var lines = content.Split('\n');
        var targetChars = _options.ChunkTargetTokens * CharsPerToken;
        var overlapChars = _options.ChunkOverlapTokens * CharsPerToken;

        // Extract and preserve YAML frontmatter
        var (frontmatter, contentStartLine) = ExtractFrontmatter(lines);

        // Build note context prefix (included in each chunk for context)
        var notePrefix = BuildNotePrefix(filePath, title, frontmatter);

        // Parse content into sections based on headers
        var sections = ParseSections(lines, contentStartLine);

        // Chunk sections
        int chunkIndex = 0;
        foreach (var chunk in ChunkSections(sections, notePrefix, targetChars, overlapChars))
        {
            yield return new ChunkInfo
            {
                Text = chunk.Text,
                ChunkIndex = chunkIndex++,
                StartLine = chunk.StartLine,
                EndLine = chunk.EndLine
            };
        }
    }

    private static (string? frontmatter, int contentStartLine) ExtractFrontmatter(string[] lines)
    {
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return (null, 0);
        }

        var sb = new StringBuilder();
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                return (sb.ToString().Trim(), i + 1);
            }
            sb.AppendLine(lines[i]);
        }

        // No closing ---, treat as no frontmatter
        return (null, 0);
    }

    private static string BuildNotePrefix(string filePath, string? title, string? frontmatter)
    {
        var sb = new StringBuilder();

        // Add note path for context
        var shortPath = Path.GetFileNameWithoutExtension(filePath);
        if (!string.IsNullOrWhiteSpace(title) && !title.Equals(shortPath, StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"[Note: {title} ({shortPath})]");
        }
        else
        {
            sb.AppendLine($"[Note: {shortPath}]");
        }

        // Extract key metadata from frontmatter (dates, tags)
        if (!string.IsNullOrWhiteSpace(frontmatter))
        {
            var dateMatch = DateRegex().Match(frontmatter);
            if (dateMatch.Success)
            {
                sb.AppendLine($"Date: {dateMatch.Groups[1].Value}");
            }

            var tagsMatch = TagsRegex().Match(frontmatter);
            if (tagsMatch.Success)
            {
                sb.AppendLine($"Tags: {tagsMatch.Groups[1].Value}");
            }
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static List<Section> ParseSections(string[] lines, int startLine)
    {
        var sections = new List<Section>();
        Section? currentSection = null;

        for (int i = startLine; i < lines.Length; i++)
        {
            var line = lines[i];
            var headerMatch = HeaderRegex().Match(line);

            if (headerMatch.Success)
            {
                // Start a new section
                if (currentSection != null)
                {
                    sections.Add(currentSection);
                }

                currentSection = new Section
                {
                    Header = line,
                    HeaderLevel = headerMatch.Groups[1].Value.Length,
                    StartLine = i,
                    Lines = new List<string>()
                };
            }
            else
            {
                if (currentSection == null)
                {
                    // Content before any header
                    currentSection = new Section
                    {
                        Header = null,
                        HeaderLevel = 0,
                        StartLine = i,
                        Lines = new List<string>()
                    };
                }

                currentSection.Lines.Add(line);
                currentSection.EndLine = i;
            }
        }

        if (currentSection != null)
        {
            sections.Add(currentSection);
        }

        return sections;
    }

    private static IEnumerable<(string Text, int StartLine, int EndLine)> ChunkSections(
        List<Section> sections,
        string notePrefix,
        int targetChars,
        int overlapChars)
    {
        var currentChunk = new StringBuilder();
        var currentStartLine = 0;
        var currentEndLine = 0;
        var prefixLength = notePrefix.Length;

        // Effective target after accounting for prefix
        var effectiveTarget = targetChars - prefixLength;

        foreach (var section in sections)
        {
            var sectionText = BuildSectionText(section);
            var sectionLength = sectionText.Length;

            // If section fits in current chunk, add it
            if (currentChunk.Length + sectionLength <= effectiveTarget)
            {
                if (currentChunk.Length == 0)
                {
                    currentStartLine = section.StartLine;
                }
                currentChunk.Append(sectionText);
                currentEndLine = section.EndLine;
            }
            else if (sectionLength > effectiveTarget)
            {
                // Section too large - need to split it
                if (currentChunk.Length > 0)
                {
                    // Yield current chunk first
                    yield return (notePrefix + currentChunk.ToString().Trim(), currentStartLine, currentEndLine);
                    currentChunk.Clear();
                }

                // Split the large section by paragraphs
                foreach (var chunk in SplitLargeSection(section, notePrefix, targetChars, overlapChars))
                {
                    yield return chunk;
                }

                currentStartLine = section.EndLine + 1;
            }
            else
            {
                // Section doesn't fit - yield current and start new
                if (currentChunk.Length > 0)
                {
                    yield return (notePrefix + currentChunk.ToString().Trim(), currentStartLine, currentEndLine);

                    // Start new chunk with overlap from end of previous
                    currentChunk.Clear();

                    // Add overlap text if possible
                    var overlapText = GetOverlapText(sectionText, overlapChars);
                    if (!string.IsNullOrWhiteSpace(overlapText))
                    {
                        currentChunk.Append(overlapText);
                    }
                }

                currentChunk.Append(sectionText);
                currentStartLine = section.StartLine;
                currentEndLine = section.EndLine;
            }
        }

        // Yield remaining content
        if (currentChunk.Length > 0)
        {
            yield return (notePrefix + currentChunk.ToString().Trim(), currentStartLine, currentEndLine);
        }
    }

    private static IEnumerable<(string Text, int StartLine, int EndLine)> SplitLargeSection(
        Section section,
        string notePrefix,
        int targetChars,
        int overlapChars)
    {
        var effectiveTarget = targetChars - notePrefix.Length;
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(section.Header))
        {
            sb.AppendLine(section.Header);
        }

        var startLine = section.StartLine;
        var currentLine = section.StartLine;

        foreach (var line in section.Lines)
        {
            currentLine++;

            // Check if adding this line would exceed target
            if (sb.Length + line.Length + 1 > effectiveTarget && sb.Length > 0)
            {
                // Yield current chunk
                yield return (notePrefix + sb.ToString().Trim(), startLine, currentLine - 1);

                // Start new chunk with overlap
                var overlap = GetOverlapText(sb.ToString(), overlapChars);
                sb.Clear();

                if (!string.IsNullOrWhiteSpace(section.Header))
                {
                    sb.AppendLine(section.Header + " (continued)");
                }

                if (!string.IsNullOrWhiteSpace(overlap))
                {
                    sb.Append(overlap);
                }

                startLine = currentLine;
            }

            sb.AppendLine(line);
        }

        if (sb.Length > 0)
        {
            yield return (notePrefix + sb.ToString().Trim(), startLine, section.EndLine);
        }
    }

    private static string BuildSectionText(Section section)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(section.Header))
        {
            sb.AppendLine(section.Header);
        }

        foreach (var line in section.Lines)
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static string GetOverlapText(string text, int overlapChars)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= overlapChars)
        {
            return text;
        }

        // Try to find a good break point (paragraph or sentence)
        var searchStart = Math.Max(0, text.Length - overlapChars - 100);
        var lastParagraph = text.LastIndexOf("\n\n", text.Length - 1, text.Length - searchStart);

        if (lastParagraph > searchStart)
        {
            return text[(lastParagraph + 2)..];
        }

        var lastSentence = text.LastIndexOf(". ", text.Length - 1, Math.Min(text.Length, overlapChars + 50));
        if (lastSentence > text.Length - overlapChars - 50)
        {
            return text[(lastSentence + 2)..];
        }

        // Fall back to character count
        return text[^overlapChars..];
    }

    private class Section
    {
        public string? Header { get; set; }
        public int HeaderLevel { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public List<string> Lines { get; set; } = new();
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"(?:^|\n)date:\s*['""]?(\d{4}-\d{2}-\d{2})['""]?", RegexOptions.IgnoreCase)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"(?:^|\n)tags:\s*\[?([^\]\n]+)\]?", RegexOptions.IgnoreCase)]
    private static partial Regex TagsRegex();
}
