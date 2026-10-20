using NxHub.Protocol;
using NxHub.Protocol.SysBotBase;

namespace NxHub.Core.Sessions;

public enum ConnectionState
{
    /// <summary>Not connected and not trying to connect.</summary>
    Disconnected,

    /// <summary>First connection attempt in progress.</summary>
    Connecting,

    /// <summary>Connected and the sys-botbase handshake succeeded.</summary>
    Connected,

    /// <summary>A connection was lost or failed; waiting for, or performing, a retry.</summary>
    Reconnecting,

    /// <summary>A user-requested disconnect is in progress.</summary>
    Disconnecting,
}

public sealed record LatencyStats(double LastMs, double AverageMs, DateTimeOffset MeasuredAt);

public sealed record ConnectionError(string Message, string? Code, DateTimeOffset At);

public sealed record ControllerSnapshot(
    IReadOnlyList<SwitchButton> HeldButtons,
    IReadOnlyDictionary<SwitchStick, StickPosition> Sticks,
    bool PendingNeutralise);

/// <summary>A point-in-time view of one Switch session.</summary>
public sealed record SwitchStatus
{
    public required string SwitchId { get; init; }

    public required ConnectionState State { get; init; }

    /// <summary>True when NX Hub is trying to keep this Switch connected.</summary>
    public required bool AutoConnect { get; init; }

    public required DateTimeOffset StateSince { get; init; }

    public DateTimeOffset? ConnectedSince { get; init; }

    public int ReconnectAttempt { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>When the most recent connection attempt started.</summary>
    public DateTimeOffset? LastAttemptAt { get; init; }

    /// <summary>Successful connections since NX Hub started (first connect plus reconnects).</summary>
    public int ConnectionCount { get; init; }

    public int ConnectionLossCount { get; init; }

    public ConnectionError? LastError { get; init; }

    public LatencyStats? Latency { get; init; }

    public SysBotCapabilities? Capabilities { get; init; }

    public required ControllerSnapshot Controller { get; init; }
}
