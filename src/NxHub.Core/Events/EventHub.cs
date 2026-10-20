using System.Collections.Concurrent;
using System.Threading.Channels;

namespace NxHub.Core.Events;

/// <summary>Well-known event types. Clients should ignore types they do not recognise.</summary>
public static class EventTypes
{
    public const string SwitchState = "switch.state";
    public const string SwitchAdded = "switch.added";
    public const string SwitchUpdated = "switch.updated";
    public const string SwitchRemoved = "switch.removed";
    public const string Activity = "activity";
    public const string ControllerState = "controller.state";
    public const string DiagnosticsCompleted = "diagnostics.completed";
    public const string MacroState = "macro.state";
    public const string MacroSaved = "macro.saved";
    public const string MacroDeleted = "macro.deleted";
}

/// <summary>An event delivered to WebSocket subscribers and in-process listeners.</summary>
public sealed record NxHubEvent(string Type, string? SwitchId, DateTimeOffset Timestamp, object? Data);

public interface IEventPublisher
{
    void Publish(NxHubEvent hubEvent);
}

/// <summary>
/// Fan-out of events to any number of subscribers. Each subscriber has a bounded buffer; a slow
/// consumer loses its oldest events rather than slowing down device sessions.
/// </summary>
public sealed class EventHub : IEventPublisher
{
    private readonly ConcurrentDictionary<Guid, Channel<NxHubEvent>> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public void Publish(NxHubEvent hubEvent)
    {
        ArgumentNullException.ThrowIfNull(hubEvent);
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(hubEvent);
        }
    }

    public EventSubscription Subscribe(int capacity = 256)
    {
        var channel = Channel.CreateBounded<NxHubEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return new EventSubscription(channel.Reader, () =>
        {
            if (_subscribers.TryRemove(id, out var removed))
            {
                removed.Writer.TryComplete();
            }
        });
    }
}

public sealed class EventSubscription : IDisposable
{
    private readonly Action _unsubscribe;
    private int _disposed;

    internal EventSubscription(ChannelReader<NxHubEvent> reader, Action unsubscribe)
    {
        Reader = reader;
        _unsubscribe = unsubscribe;
    }

    public ChannelReader<NxHubEvent> Reader { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _unsubscribe();
        }
    }
}
