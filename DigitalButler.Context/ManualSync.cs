using DigitalButler.Data;

namespace DigitalButler.Context;

public interface IManualSyncRunner
{
    Task<ManualSyncResult> RunAllAsync(CancellationToken ct = default);
}

public sealed record ManualSyncResult(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int UpdatersRun,
    int Failures,
    IReadOnlyList<string> Messages
);
