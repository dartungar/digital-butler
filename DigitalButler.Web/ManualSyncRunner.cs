using DigitalButler.Context;
using DigitalButler.Common;
using DigitalButler.Skills.VaultSearch;

namespace DigitalButler.Web;

public sealed class ManualSyncRunner : IManualSyncRunner
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly IServiceScopeFactory _scopeFactory;

    public ManualSyncRunner(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<ManualSyncResult> RunAllAsync(CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        await _mutex.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IContextUpdaterRegistry>();
            var updaters = registry.GetAll().ToList();

            var messages = new List<string>();
            var failures = 0;

            foreach (var updater in updaters)
            {
                try
                {
                    await updater.UpdateAsync(ct);
                    messages.Add($"{updater.Source}: ok");
                }
                catch (Exception ex)
                {
                    failures++;
                    messages.Add($"{updater.Source}: failed ({ex.GetType().Name})");
                }
            }

            // Also run vault indexing as part of sync
            var vaultIndexer = scope.ServiceProvider.GetService<IVaultIndexer>();
            if (vaultIndexer != null)
            {
                try
                {
                    var result = await vaultIndexer.IndexVaultAsync(ct);
                    messages.Add($"VaultSearch: indexed {result.NotesAdded} new, {result.NotesUpdated} updated, {result.ChunksCreated} chunks");
                }
                catch (Exception ex)
                {
                    failures++;
                    messages.Add($"VaultSearch: failed ({ex.GetType().Name})");
                }
            }

            var finishedAt = DateTimeOffset.UtcNow;
            return new ManualSyncResult(startedAt, finishedAt, updaters.Count, failures, messages);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ManualSyncResult> RunSourceAsync(ContextSource source, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        await _mutex.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IContextUpdaterRegistry>();
            var updater = registry.GetUpdater(source);

            var messages = new List<string>();
            var failures = 0;
            var updatersRun = 0;

            if (updater is null)
            {
                messages.Add($"{source}: not registered");
                failures = 1;
            }
            else
            {
                updatersRun = 1;
                try
                {
                    await updater.UpdateAsync(ct);
                    messages.Add($"{source}: ok");
                }
                catch (Exception ex)
                {
                    failures++;
                    messages.Add($"{source}: failed ({ex.GetType().Name}: {ex.Message})");
                }
            }

            var finishedAt = DateTimeOffset.UtcNow;
            return new ManualSyncResult(startedAt, finishedAt, updatersRun, failures, messages);
        }
        finally
        {
            _mutex.Release();
        }
    }
}
