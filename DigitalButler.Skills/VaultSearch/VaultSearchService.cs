using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DigitalButler.Common;
using DigitalButler.Data.Repositories;

namespace DigitalButler.Skills.VaultSearch;

public interface IVaultSearchService
{
    Task<IReadOnlyList<VaultSearchResult>> SearchAsync(
        string query,
        int? topK = null,
        float? minScore = null,
        CancellationToken ct = default);

    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    Task<VaultSearchStats> GetStatsAsync(CancellationToken ct = default);
    Task<string> DebugSearchAsync(string query, CancellationToken ct = default);
}

public class VaultSearchStats
{
    public int IndexedNotes { get; set; }
    public int IndexedChunks { get; set; }
    public bool VecExtensionAvailable { get; set; }
}

public class VaultSearchService : IVaultSearchService
{
    private readonly VaultSearchRepository _repo;
    private readonly IEmbeddingService _embeddingService;
    private readonly IDateQueryTranslator _dateTranslator;
    private readonly VaultSearchOptions _options;
    private readonly ILogger<VaultSearchService> _logger;

    public VaultSearchService(
        VaultSearchRepository repo,
        IEmbeddingService embeddingService,
        IDateQueryTranslator dateTranslator,
        IOptions<VaultSearchOptions> options,
        ILogger<VaultSearchService> logger)
    {
        _repo = repo;
        _embeddingService = embeddingService;
        _dateTranslator = dateTranslator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VaultSearchResult>> SearchAsync(
        string query,
        int? topK = null,
        float? minScore = null,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("Vault search is disabled");
            return Array.Empty<VaultSearchResult>();
        }

        var effectiveTopK = topK ?? _options.TopK;
        var effectiveMinScore = minScore ?? _options.MinScore;

        // Translate date references in the query
        var translatedQuery = _dateTranslator.TranslateQuery(query, DateTimeOffset.Now);

        _logger.LogDebug(
            "Searching vault: original='{Query}', dateTerms=[{DateTerms}], topK={TopK}, minScore={MinScore}",
            query,
            string.Join(", ", translatedQuery.DateTerms),
            effectiveTopK,
            effectiveMinScore);

        // Generate embedding for the combined query
        var searchQuery = translatedQuery.CombinedQuery;
        var queryEmbedding = await _embeddingService.GetEmbeddingAsync(searchQuery, ct);

        // Search with sqlite-vec
        var results = await _repo.SearchAsync(queryEmbedding, effectiveTopK * 2, effectiveMinScore, ct);

        // Deduplicate results from the same note (keep highest scoring chunk)
        var deduped = results
            .GroupBy(r => r.FilePath)
            .Select(g => g.OrderByDescending(r => r.Score).First())
            .OrderByDescending(r => r.Score)
            .Take(effectiveTopK)
            .ToList();

        _logger.LogDebug("Vault search returned {Count} results (from {Raw} raw)", deduped.Count, results.Count);

        return deduped;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return false;

        return await _repo.IsVecAvailableAsync(ct);
    }

    public async Task<VaultSearchStats> GetStatsAsync(CancellationToken ct = default)
    {
        return new VaultSearchStats
        {
            IndexedNotes = await _repo.GetIndexedNoteCountAsync(ct),
            IndexedChunks = await _repo.GetIndexedChunkCountAsync(ct),
            VecExtensionAvailable = await _repo.IsVecAvailableAsync(ct)
        };
    }

    public async Task<string> DebugSearchAsync(string query, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== Debug Search for: {query} ===");
        sb.AppendLine($"VaultSearch.Enabled: {_options.Enabled}");
        sb.AppendLine($"VaultSearch.TopK: {_options.TopK}");
        sb.AppendLine($"VaultSearch.MinScore: {_options.MinScore}");
        sb.AppendLine();

        try
        {
            // Generate embedding
            sb.AppendLine("Generating embedding...");
            var embedding = await _embeddingService.GetEmbeddingAsync(query, ct);
            sb.AppendLine($"Embedding generated: {embedding.Length} dimensions");
            sb.AppendLine($"First 5 values: [{string.Join(", ", embedding.Take(5).Select(f => f.ToString("F6")))}]");
            sb.AppendLine();

            // Run debug query
            sb.AppendLine("Running debug query...");
            var debugResult = await _repo.DebugSearchAsync(embedding, 5, ct);
            sb.AppendLine(debugResult);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine(ex.StackTrace);
        }

        return sb.ToString();
    }
}
