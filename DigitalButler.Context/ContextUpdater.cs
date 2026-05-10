using Microsoft.Extensions.Logging;
using DigitalButler.Common;
using DigitalButler.Data.Repositories;

namespace DigitalButler.Context;

public interface IStaleContextCleanupSource
{
    bool CanCleanStaleItems { get; }
    DateTimeOffset? CleanupWindowStartUtc { get; }
    DateTimeOffset? CleanupWindowEndUtc { get; }
}

public interface ICategorizedStaleContextCleanupSource : IStaleContextCleanupSource
{
    IReadOnlyCollection<string?> CleanupCategories { get; }
}

public sealed class ContextUpdater : IContextUpdater
{
    private readonly IContextSource _source;
    private readonly ContextRepository _repo;
    private readonly ILogger<ContextUpdater> _logger;

    public ContextUpdater(IContextSource source, ContextRepository repo, ILogger<ContextUpdater> logger)
    {
        _source = source;
        _repo = repo;
        _logger = logger;
    }

    public ContextSource Source => _source.Source;

    public async Task UpdateAsync(CancellationToken ct = default)
    {
        var items = await _source.FetchAsync(ct);
        _logger.LogInformation("Fetched {Count} items from {Source}", items.Count, _source.Source);

        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            if (item.CreatedAt == default) item.CreatedAt = now;
            item.UpdatedAt = now;
        }

        await _repo.UpsertByExternalIdAsync(items, ct);

        if (_source is IStaleContextCleanupSource { CanCleanStaleItems: true } cleanup)
        {
            var categories = ResolveCleanupCategories(items, cleanup);

            var deleted = 0;
            foreach (var category in categories)
            {
                var currentExternalIds = items
                    .Where(i => CategoriesMatch(i.Category, category))
                    .Select(i => i.ExternalId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .ToList();

                deleted += await _repo.DeleteMissingExternalIdsAsync(
                    _source.Source,
                    currentExternalIds,
                    cleanup.CleanupWindowStartUtc,
                    cleanup.CleanupWindowEndUtc,
                    category.Value,
                    filterByCategory: category.HasValue,
                    ct: ct);
            }

            if (deleted > 0)
            {
                _logger.LogInformation("Removed {Count} stale context items from {Source}", deleted, _source.Source);
            }
        }
    }

    private static List<OptionalCategory> ResolveCleanupCategories(
        IReadOnlyList<ContextItem> items,
        IStaleContextCleanupSource cleanup)
    {
        if (cleanup is ICategorizedStaleContextCleanupSource categorized &&
            categorized.CleanupCategories.Count > 0)
        {
            return categorized.CleanupCategories
                .Select(c => new OptionalCategory(c))
                .Distinct()
                .ToList();
        }

        if (items.Count == 0)
        {
            return new List<OptionalCategory> { OptionalCategory.Any };
        }

        return items
            .Select(i => new OptionalCategory(i.Category))
            .Distinct()
            .ToList();
    }

    private static bool CategoriesMatch(string? itemCategory, OptionalCategory cleanupCategory)
    {
        if (!cleanupCategory.HasValue)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(cleanupCategory.Value))
        {
            return string.IsNullOrWhiteSpace(itemCategory);
        }

        return string.Equals(itemCategory, cleanupCategory.Value, StringComparison.Ordinal);
    }

    private readonly record struct OptionalCategory(string? Value, bool HasValue)
    {
        public OptionalCategory(string? value)
            : this(value, true)
        {
        }

        public static OptionalCategory Any => new(null, false);
    }
}
