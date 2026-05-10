using System.Collections.Concurrent;
using DigitalButler.Skills;

namespace DigitalButler.Telegram.State;

public enum ObsidianCaptureTarget
{
    Inbox,
    DailyNote
}

public class ConversationStateManager
{
    private readonly ConcurrentDictionary<long, ConversationState> _states = new();
    private readonly TimeSpan _defaultTtl = TimeSpan.FromMinutes(5);

    public void SetPendingCalendarEvent(long chatId, ParsedCalendarEvent parsed)
    {
        var state = GetOrCreateState(chatId);
        state.PendingCalendarEvent = parsed;
        state.PendingCalendarEventAt = DateTimeOffset.UtcNow;
    }

    public ParsedCalendarEvent? GetAndRemovePendingCalendarEvent(long chatId)
    {
        if (!_states.TryGetValue(chatId, out var state))
            return null;

        var parsed = state.PendingCalendarEvent;
        if (parsed is null)
            return null;

        if (state.PendingCalendarEventAt.HasValue &&
            (DateTimeOffset.UtcNow - state.PendingCalendarEventAt.Value) > _defaultTtl)
        {
            state.PendingCalendarEvent = null;
            state.PendingCalendarEventAt = null;
            return null;
        }

        state.PendingCalendarEvent = null;
        state.PendingCalendarEventAt = null;
        return parsed;
    }

    public void ClearPendingCalendarEvent(long chatId)
    {
        if (_states.TryGetValue(chatId, out var state))
        {
            state.PendingCalendarEvent = null;
            state.PendingCalendarEventAt = null;
        }
    }

    public void SetPendingObsidianCapture(long chatId, ObsidianCaptureTarget target)
    {
        var state = GetOrCreateState(chatId);
        state.PendingObsidianCaptureTarget = target;
        state.PendingObsidianCaptureAt = DateTimeOffset.UtcNow;
    }

    public ObsidianCaptureTarget? GetAndRemovePendingObsidianCapture(long chatId)
    {
        if (!_states.TryGetValue(chatId, out var state))
            return null;

        var target = state.PendingObsidianCaptureTarget;
        if (target is null)
            return null;

        if (state.PendingObsidianCaptureAt.HasValue &&
            (DateTimeOffset.UtcNow - state.PendingObsidianCaptureAt.Value) > _defaultTtl)
        {
            state.PendingObsidianCaptureTarget = null;
            state.PendingObsidianCaptureAt = null;
            return null;
        }

        state.PendingObsidianCaptureTarget = null;
        state.PendingObsidianCaptureAt = null;
        return target;
    }

    public void CleanupExpired()
    {
        var expiredChats = _states
            .Where(kvp => IsStateExpired(kvp.Value))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var chatId in expiredChats)
        {
            _states.TryRemove(chatId, out _);
        }
    }

    private bool IsStateExpired(ConversationState state)
    {
        var now = DateTimeOffset.UtcNow;
        var calendarExpired = state.PendingCalendarEvent is null ||
                              (state.PendingCalendarEventAt.HasValue && (now - state.PendingCalendarEventAt.Value) > _defaultTtl);
        var obsidianExpired = state.PendingObsidianCaptureTarget is null ||
                              (state.PendingObsidianCaptureAt.HasValue && (now - state.PendingObsidianCaptureAt.Value) > _defaultTtl);

        return calendarExpired && obsidianExpired;
    }

    private ConversationState GetOrCreateState(long chatId)
    {
        return _states.GetOrAdd(chatId, _ => new ConversationState());
    }

    private class ConversationState
    {
        public ParsedCalendarEvent? PendingCalendarEvent { get; set; }
        public DateTimeOffset? PendingCalendarEventAt { get; set; }
        public ObsidianCaptureTarget? PendingObsidianCaptureTarget { get; set; }
        public DateTimeOffset? PendingObsidianCaptureAt { get; set; }
    }
}
