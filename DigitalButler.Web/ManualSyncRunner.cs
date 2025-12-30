using DigitalButler.Context;
using DigitalButler.Data;

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

            var finishedAt = DateTimeOffset.UtcNow;
            return new ManualSyncResult(startedAt, finishedAt, updaters.Count, failures, messages);
        }
        finally
        {
            _mutex.Release();
        }
    }
}
