using System.Text;
using DigitalButler.Common;
using DigitalButler.Skills.VaultSearch;
using Microsoft.Extensions.Logging;

namespace DigitalButler.Telegram.Skills;

public sealed class VaultSearchSkillExecutor : IVaultSearchSkillExecutor
{
    private readonly IVaultSearchService _searchService;
    private readonly IVaultEnrichmentService _vaultEnrichment;
    private readonly ILogger<VaultSearchSkillExecutor> _logger;

    public VaultSearchSkillExecutor(
        IVaultSearchService searchService,
        IVaultEnrichmentService vaultEnrichment,
        ILogger<VaultSearchSkillExecutor> logger)
    {
        _searchService = searchService;
        _vaultEnrichment = vaultEnrichment;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Please provide a search query. Example: /search home renovation";
        }

        var isAvailable = await _searchService.IsAvailableAsync(ct);
        if (!isAvailable)
        {
            return "Vault search is not available. The vault may not be indexed yet. Use /sync to start indexing.";
        }

        _logger.LogInformation("Executing vault search for query: {Query}", query);

        var results = await _searchService.SearchAsync(query, ct: ct);

        if (results.Count == 0)
        {
            return $"No notes found matching \"{query}\". Try different keywords or check if the vault is indexed.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} relevant note(s) for \"{query}\":");
        sb.AppendLine();

        var citations = new List<ObsidianCitation>();
        foreach (var result in results)
        {
            var title = result.Title ?? Path.GetFileNameWithoutExtension(result.FilePath);
            var score = result.Score * 100;

            sb.AppendLine($"**{title}** ({score:F0}% match)");
            sb.AppendLine($"📁 {result.FilePath}");

            // Show a snippet of the matching content
            var snippet = GetSnippet(result.ChunkText, 300);
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                sb.AppendLine($"```\n{snippet}\n```");
            }

            sb.AppendLine();

            citations.Add(new ObsidianCitation
            {
                Title = title,
                FilePath = result.FilePath,
                NoteDate = TryParseDateFromPath(result.FilePath)
            });
        }

        // Add clickable Obsidian links
        sb.Append(_vaultEnrichment.FormatCitations(citations, maxCitations: 10));

        return sb.ToString().Trim();
    }

    private static DateOnly? TryParseDateFromPath(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (DateOnly.TryParse(fileName, out var date))
        {
            return date;
        }
        return null;
    }

    public async Task<string> GetStatsAsync(CancellationToken ct)
    {
        var stats = await _searchService.GetStatsAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("Vault Search Stats:");
        sb.AppendLine($"- Indexed notes: {stats.IndexedNotes}");
        sb.AppendLine($"- Indexed chunks: {stats.IndexedChunks}");
        sb.AppendLine($"- Vector extension: {(stats.VecExtensionAvailable ? "available" : "not available")}");

        return sb.ToString();
    }

    private static string GetSnippet(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Remove the note prefix line if present
        var lines = text.Split('\n');
        var contentStart = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("[Note:") || string.IsNullOrWhiteSpace(lines[i]))
            {
                contentStart = i + 1;
            }
            else
            {
                break;
            }
        }

        var content = string.Join("\n", lines.Skip(contentStart));

        if (content.Length <= maxLength)
            return content.Trim();

        // Find a good break point
        var cutoff = content.LastIndexOf('.', maxLength);
        if (cutoff < maxLength / 2)
            cutoff = content.LastIndexOf(' ', maxLength);
        if (cutoff < maxLength / 2)
            cutoff = maxLength;

        return content[..cutoff].Trim() + "...";
    }
}
