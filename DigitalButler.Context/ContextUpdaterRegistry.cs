using DigitalButler.Context.Repositories;
using Microsoft.Extensions.Logging;

namespace DigitalButler.Context;

/// <summary>
/// Registry for context updaters, allowing lookup by source.
/// Solves the issue of multiple IContextUpdater registrations not being distinguishable.
/// </summary>
public interface IContextUpdaterRegistry
{
    IContextUpdater? GetUpdater(ContextSource source);
    IEnumerable<IContextUpdater> GetAll();
    IEnumerable<ContextSource> GetRegisteredSources();
}

public sealed class ContextUpdaterRegistry : IContextUpdaterRegistry
{
    private readonly Dictionary<ContextSource, IContextUpdater> _updaters;

    public ContextUpdaterRegistry(
        GoogleCalendarContextSource googleCalendarSource,
        GmailContextSource gmailSource,
        ContextRepository contextRepository,
        ILogger<ContextUpdater> logger)
    {
        _updaters = new Dictionary<ContextSource, IContextUpdater>
        {
            [ContextSource.GoogleCalendar] = new ContextUpdater(googleCalendarSource, contextRepository, logger),
            [ContextSource.Gmail] = new ContextUpdater(gmailSource, contextRepository, logger),
        };
    }

    public IContextUpdater? GetUpdater(ContextSource source) =>
        _updaters.TryGetValue(source, out var updater) ? updater : null;

    public IEnumerable<IContextUpdater> GetAll() => _updaters.Values;

    public IEnumerable<ContextSource> GetRegisteredSources() => _updaters.Keys;
}
