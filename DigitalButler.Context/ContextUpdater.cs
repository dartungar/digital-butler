using Microsoft.Extensions.Logging;
using DigitalButler.Common;
using DigitalButler.Data.Repositories;

namespace DigitalButler.Context;

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
    }
}
