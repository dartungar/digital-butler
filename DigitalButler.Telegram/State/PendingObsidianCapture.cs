using DigitalButler.Common;

namespace DigitalButler.Telegram.State;

public sealed class PendingObsidianCapture
{
    public ObsidianCaptureRequest Request { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
