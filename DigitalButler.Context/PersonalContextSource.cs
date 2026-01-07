using DigitalButler.Common;

namespace DigitalButler.Context;

/// <summary>
/// Personal context is added directly via UI/Telegram and stored immediately, so the updater has nothing to pull.
/// This stub satisfies the IContextSource contract without duplicating data.
/// </summary>
public class PersonalContextSource : IContextSource
{
    public ContextSource Source => ContextSource.Personal;

    public Task<IReadOnlyList<ContextItem>> FetchAsync(CancellationToken ct = default)
    {
        // No external fetch; personal notes are already persisted when added by the user.
        return Task.FromResult<IReadOnlyList<ContextItem>>(Array.Empty<ContextItem>());
    }
}

