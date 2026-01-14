using System.Collections.Concurrent;
using DigitalButler.Skills;

namespace DigitalButler.Telegram.State;

public class ConversationStateManager
{
    private readonly ConcurrentDictionary<long, ConversationState> _states = new();
    private readonly TimeSpan _defaultTtl = TimeSpan.FromMinutes(5);

    public void SetAwaitingDrawingSubject(long chatId, bool awaiting)
    {
        var state = GetOrCreateState(chatId);
        state.AwaitingDrawingSubject = awaiting;
        if (awaiting)
            state.AwaitingDrawingSubjectAt = DateTimeOffset.UtcNow;
    }

    public bool IsAwaitingDrawingSubject(long chatId)
    {
        if (!_states.TryGetValue(chatId, out var state))
            return false;

        if (!state.AwaitingDrawingSubject)
            return false;

        // Check TTL
        if (state.AwaitingDrawingSubjectAt.HasValue &&
            (DateTimeOffset.UtcNow - state.AwaitingDrawingSubjectAt.Value) > _defaultTtl)
        {
            state.AwaitingDrawingSubject = false;
            return false;
        }

        return true;
    }

    public void ClearAwaitingDrawingSubject(long chatId)
    {
        if (_states.TryGetValue(chatId, out var state))
        {
            state.AwaitingDrawingSubject = false;
            state.AwaitingDrawingSubjectAt = null;
        }
    }

    public void SetPendingDrawingTopic(long chatId, string topic)
    {
        var state = GetOrCreateState(chatId);
        state.PendingDrawingTopic = topic;
        state.PendingDrawingTopicAt = DateTimeOffset.UtcNow;
    }

    public string? GetAndRemovePendingDrawingTopic(long chatId)
    {
        if (!_states.TryGetValue(chatId, out var state))
            return null;

        var topic = state.PendingDrawingTopic;
        if (topic is null)
            return null;

        // Check TTL
        if (state.PendingDrawingTopicAt.HasValue &&
            (DateTimeOffset.UtcNow - state.PendingDrawingTopicAt.Value) > _defaultTtl)
        {
            state.PendingDrawingTopic = null;
            return null;
        }

        state.PendingDrawingTopic = null;
        state.PendingDrawingTopicAt = null;
        return topic;
    }

    public string? PeekPendingDrawingTopic(long chatId)
    {
        if (!_states.TryGetValue(chatId, out var state))
            return null;

        if (state.PendingDrawingTopic is null)
            return null;

        // Check TTL
        if (state.PendingDrawingTopicAt.HasValue &&
            (DateTimeOffset.UtcNow - state.PendingDrawingTopicAt.Value) > _defaultTtl)
        {
            state.PendingDrawingTopic = null;
            return null;
        }

        return state.PendingDrawingTopic;
    }

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

        // Check TTL
        if (state.PendingCalendarEventAt.HasValue &&
            (DateTimeOffset.UtcNow - state.PendingCalendarEventAt.Value) > _defaultTtl)
        {
            state.PendingCalendarEvent = null;
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

    public void SetLastDrawingSubject(long chatId, string subject)
    {
        var state = GetOrCreateState(chatId);
        state.LastDrawingSubject = subject;
    }

    public string? GetLastDrawingSubject(long chatId)
    {
        return _states.TryGetValue(chatId, out var state) ? state.LastDrawingSubject : null;
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
        // State is expired if all pending items are either null or past TTL
        var now = DateTimeOffset.UtcNow;

        var drawingSubjectExpired = !state.AwaitingDrawingSubject ||
            (state.AwaitingDrawingSubjectAt.HasValue && (now - state.AwaitingDrawingSubjectAt.Value) > _defaultTtl);

        var drawingTopicExpired = state.PendingDrawingTopic is null ||
            (state.PendingDrawingTopicAt.HasValue && (now - state.PendingDrawingTopicAt.Value) > _defaultTtl);

        var calendarEventExpired = state.PendingCalendarEvent is null ||
            (state.PendingCalendarEventAt.HasValue && (now - state.PendingCalendarEventAt.Value) > _defaultTtl);

        return drawingSubjectExpired && drawingTopicExpired && calendarEventExpired;
    }

    private ConversationState GetOrCreateState(long chatId)
    {
        return _states.GetOrAdd(chatId, _ => new ConversationState());
    }

    private class ConversationState
    {
        public bool AwaitingDrawingSubject { get; set; }
        public DateTimeOffset? AwaitingDrawingSubjectAt { get; set; }
        public string? PendingDrawingTopic { get; set; }
        public DateTimeOffset? PendingDrawingTopicAt { get; set; }
        public ParsedCalendarEvent? PendingCalendarEvent { get; set; }
        public DateTimeOffset? PendingCalendarEventAt { get; set; }
        public string? LastDrawingSubject { get; set; }
    }
}
