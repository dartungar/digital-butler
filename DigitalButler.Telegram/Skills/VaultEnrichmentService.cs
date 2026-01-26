using System.Text;
using DigitalButler.Common;
using DigitalButler.Context;
using DigitalButler.Data.Repositories;
using DigitalButler.Skills.VaultSearch;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalButler.Telegram.Skills;

public interface IVaultEnrichmentService
{
    /// <summary>
    /// Enriches a query with vault context based on semantic search and/or date range.
    /// </summary>
    Task<VaultEnrichmentResult> EnrichAsync(
        string? query,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the vault name for building Obsidian URIs.
    /// </summary>
    string VaultName { get; }

    /// <summary>
    /// Formats citations as Obsidian links.
    /// </summary>
    string FormatCitations(IReadOnlyList<ObsidianCitation> citations, int maxCitations = 5);
}

public class VaultEnrichmentService : IVaultEnrichmentService
{
    private readonly IVaultSearchService _searchService;
    private readonly ObsidianDailyNotesRepository _dailyNotesRepo;
    private readonly ObsidianOptions _options;
    private readonly ILogger<VaultEnrichmentService> _logger;

    public VaultEnrichmentService(
        IVaultSearchService searchService,
        ObsidianDailyNotesRepository dailyNotesRepo,
        IOptions<ObsidianOptions> options,
        ILogger<VaultEnrichmentService> logger)
    {
        _searchService = searchService;
        _dailyNotesRepo = dailyNotesRepo;
        _options = options.Value;
        _logger = logger;
    }

    public string VaultName => _options.VaultName;

    public async Task<VaultEnrichmentResult> EnrichAsync(
        string? query,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken ct = default)
    {
        var result = new VaultEnrichmentResult();

        // If we have a date range, fetch daily notes for that period
        if (startDate.HasValue && endDate.HasValue)
        {
            _logger.LogDebug(
                "Fetching daily notes for date range {Start} to {End}",
                startDate.Value, endDate.Value);

            var dailyNotes = await _dailyNotesRepo.GetRangeAsync(startDate.Value, endDate.Value, ct);

            foreach (var note in dailyNotes)
            {
                var contextItem = ConvertDailyNoteToContextItem(note);
                result.ContextItems.Add(contextItem);

                result.Citations.Add(new ObsidianCitation
                {
                    Title = note.Date.ToString("yyyy-MM-dd"),
                    FilePath = note.FilePath,
                    NoteDate = note.Date
                });
            }

            _logger.LogDebug("Found {Count} daily notes for date range", dailyNotes.Count);
        }

        // Only run semantic search if we DON'T have a date range
        // When user asks a temporal question ("what did I do in January?"), we want daily notes, not semantic matches
        if (!string.IsNullOrWhiteSpace(query) && !startDate.HasValue && !endDate.HasValue)
        {
            var isAvailable = await _searchService.IsAvailableAsync(ct);
            if (isAvailable)
            {
                _logger.LogDebug("Running semantic search for query: {Query}", query);
                var searchResults = await _searchService.SearchAsync(query, topK: 5, ct: ct);

                foreach (var searchResult in searchResults)
                {
                    // Skip if we already have this note from date range
                    if (result.Citations.Any(c => c.FilePath == searchResult.FilePath))
                        continue;

                    var contextItem = ConvertSearchResultToContextItem(searchResult);
                    result.ContextItems.Add(contextItem);

                    result.Citations.Add(new ObsidianCitation
                    {
                        Title = searchResult.Title ?? Path.GetFileNameWithoutExtension(searchResult.FilePath),
                        FilePath = searchResult.FilePath,
                        NoteDate = TryParseDateFromPath(searchResult.FilePath)
                    });
                }

                _logger.LogDebug("Found {Count} search results", searchResults.Count);
            }
            else
            {
                _logger.LogDebug("Vault search is not available");
            }
        }

        return result;
    }

    public string FormatCitations(IReadOnlyList<ObsidianCitation> citations, int maxCitations = 5)
    {
        return CitationFormatter.FormatCitations(citations, _options.VaultName, maxCitations);
    }

    private static ContextItem ConvertDailyNoteToContextItem(ObsidianDailyNote note)
    {
        var sb = new StringBuilder();

        // Add journal notes if available
        if (!string.IsNullOrWhiteSpace(note.Notes))
        {
            sb.AppendLine("Journal:");
            sb.AppendLine(TruncateText(note.Notes, 500));
            sb.AppendLine();
        }

        // Add completed tasks
        if (note.CompletedTasks?.Count > 0)
        {
            sb.AppendLine("Completed tasks:");
            foreach (var task in note.CompletedTasks.Take(10))
            {
                sb.AppendLine($"- {task}");
            }
            sb.AppendLine();
        }

        // Add key metrics summary if available
        var metrics = new List<string>();
        if (note.Energy.HasValue) metrics.Add($"Energy: {note.Energy}");
        if (note.Motivation.HasValue) metrics.Add($"Motivation: {note.Motivation}");
        if (note.Stress.HasValue) metrics.Add($"Stress: {note.Stress}");
        if (note.LifeSatisfaction.HasValue) metrics.Add($"Life satisfaction: {note.LifeSatisfaction}");

        if (metrics.Count > 0)
        {
            sb.AppendLine("Metrics: " + string.Join(", ", metrics));
        }

        return new ContextItem
        {
            Id = Guid.NewGuid(),
            Source = ContextSource.Obsidian,
            Title = $"Daily note: {note.Date:yyyy-MM-dd}",
            Body = sb.ToString().Trim(),
            RelevantDate = new DateTimeOffset(note.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            IsTimeless = false,
            ExternalId = $"obsidian:daily:{note.Date:yyyy-MM-dd}",
            Category = "Daily Notes"
        };
    }

    private static ContextItem ConvertSearchResultToContextItem(VaultSearchResult result)
    {
        return new ContextItem
        {
            Id = Guid.NewGuid(),
            Source = ContextSource.Obsidian,
            Title = result.Title ?? Path.GetFileNameWithoutExtension(result.FilePath),
            Body = TruncateText(result.ChunkText, 500),
            IsTimeless = true,
            ExternalId = $"obsidian:search:{result.FilePath}",
            Category = "Vault Search"
        };
    }

    private static DateOnly? TryParseDateFromPath(string filePath)
    {
        // Try to parse date from filename (e.g., "2026-01-20.md")
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (DateOnly.TryParse(fileName, out var date))
        {
            return date;
        }
        return null;
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;

        var cutoff = text.LastIndexOf('.', maxLength);
        if (cutoff < maxLength / 2)
            cutoff = text.LastIndexOf(' ', maxLength);
        if (cutoff < maxLength / 2)
            cutoff = maxLength;

        return text[..cutoff] + "...";
    }
}
