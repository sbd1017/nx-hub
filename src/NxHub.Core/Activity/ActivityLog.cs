using NxHub.Core.Events;

namespace NxHub.Core.Activity;

public enum ActivityLevel
{
    Info,
    Warning,
    Error,
}

public enum ActivityCategory
{
    Connection,
    Controller,
    Diagnostics,
    Protocol,
    Configuration,
}

public sealed record ActivityEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    ActivityLevel Level,
    ActivityCategory Category,
    string Message);

/// <summary>
/// A small, bounded, human-readable history per Switch ("recent activity" in the UI).
/// Structured logs remain the source of truth; this is a convenience view.
/// </summary>
public sealed class ActivityLog
{
    private readonly object _gate = new();
    private readonly Queue<ActivityEntry> _entries;
    private readonly int _capacity;
    private readonly string _switchId;
    private readonly IEventPublisher _events;
    private readonly TimeProvider _time;
    private long _sequence;

    public ActivityLog(string switchId, IEventPublisher events, TimeProvider time, int capacity = 200)
    {
        _switchId = switchId;
        _events = events;
        _time = time;
        _capacity = capacity;
        _entries = new Queue<ActivityEntry>(capacity);
    }

    public ActivityEntry Add(ActivityLevel level, ActivityCategory category, string message)
    {
        ActivityEntry entry;
        lock (_gate)
        {
            entry = new ActivityEntry(++_sequence, _time.GetUtcNow(), level, category, message);
            if (_entries.Count == _capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }

        _events.Publish(new NxHubEvent(EventTypes.Activity, _switchId, entry.Timestamp, entry));
        return entry;
    }

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<ActivityEntry> GetRecent(int limit = 50)
    {
        lock (_gate)
        {
            return _entries.Reverse().Take(Math.Clamp(limit, 1, _capacity)).ToArray();
        }
    }
}
