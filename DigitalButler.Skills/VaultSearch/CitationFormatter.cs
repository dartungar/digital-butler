using System.Text;
using System.Web;
using DigitalButler.Common;

namespace DigitalButler.Skills.VaultSearch;

/// <summary>
/// Formats Obsidian citations for inclusion in responses.
/// </summary>
public static class CitationFormatter
{
    /// <summary>
    /// Formats a list of citations as Obsidian protocol links.
    /// </summary>
    public static string FormatCitations(IReadOnlyList<ObsidianCitation> citations, string vaultName, int maxCitations = 5)
    {
        if (citations.Count == 0 || string.IsNullOrWhiteSpace(vaultName))
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("Sources:");

        var citationsToShow = citations.Take(maxCitations).ToList();
        foreach (var citation in citationsToShow)
        {
            var uri = BuildObsidianUri(vaultName, citation.FilePath);
            var displayTitle = GetDisplayTitle(citation);
            sb.AppendLine($"- [[{displayTitle}]]({uri})");
        }

        if (citations.Count > maxCitations)
        {
            sb.AppendLine($"- ...and {citations.Count - maxCitations} more");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds an Obsidian protocol URI for opening a note.
    /// </summary>
    public static string BuildObsidianUri(string vaultName, string filePath)
    {
        // Obsidian URI format: obsidian://open?vault=VaultName&file=path/to/note.md
        var encodedVault = HttpUtility.UrlEncode(vaultName);
        var encodedFile = HttpUtility.UrlEncode(filePath);
        return $"obsidian://open?vault={encodedVault}&file={encodedFile}";
    }

    /// <summary>
    /// Gets a display title for a citation, preferring the note date if available.
    /// </summary>
    private static string GetDisplayTitle(ObsidianCitation citation)
    {
        if (citation.NoteDate.HasValue)
        {
            return citation.NoteDate.Value.ToString("yyyy-MM-dd");
        }

        if (!string.IsNullOrWhiteSpace(citation.Title))
        {
            return citation.Title;
        }

        // Fall back to filename without extension
        return Path.GetFileNameWithoutExtension(citation.FilePath);
    }
}
