using DigitalButler.Common;

namespace DigitalButler.Context;

public interface IManualSyncRunner
{
    Task<ManualSyncResult> RunAllAsync(CancellationToken ct = default);
    Task<ManualSyncResult> RunSourceAsync(ContextSource source, CancellationToken ct = default);
}

public sealed record ManualSyncResult(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int UpdatersRun,
    int Failures,
    IReadOnlyList<string> Messages
);
