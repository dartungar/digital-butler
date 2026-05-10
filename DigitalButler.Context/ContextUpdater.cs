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
            var currentExternalIds = items
                .Select(i => i.ExternalId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToList();

            var deleted = await _repo.DeleteMissingExternalIdsAsync(
                _source.Source,
                currentExternalIds,
                cleanup.CleanupWindowStartUtc,
                cleanup.CleanupWindowEndUtc,
                ct);

            if (deleted > 0)
            {
                _logger.LogInformation("Removed {Count} stale context items from {Source}", deleted, _source.Source);
            }
        }
    }
}
