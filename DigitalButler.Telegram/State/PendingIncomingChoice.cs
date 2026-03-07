using DigitalButler.Common;

namespace DigitalButler.Telegram.State;

public enum PendingIncomingKind
{
    Text,
    Photo,
    Voice
}

public sealed class PendingIncomingChoice
{
    public PendingIncomingKind Kind { get; init; }
    public string? RoutingText { get; init; }
    public string? TelegramFileId { get; init; }
    public string? CaptionOrText { get; init; }
    public string MediaFileExtension { get; init; } = ".jpg";
    public ObsidianCaptureRequest? CaptureRequest { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
