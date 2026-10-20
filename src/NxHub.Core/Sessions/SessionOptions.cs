using NxHub.Protocol.SysBotBase;

namespace NxHub.Core.Sessions;

public enum NeutraliseMode
{
    /// <summary>On (re)connect, release only inputs NX Hub believes were still held when the connection dropped.</summary>
    OnlyIfHeld,

    /// <summary>On every connect, release every button and centre both sticks. Attaches the virtual controller.</summary>
    Always,

    /// <summary>Never send inputs automatically on connect.</summary>
    Never,
}

public sealed record ControllerOptions
{
    public TimeSpan DefaultPressDuration { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Games poll input at 30–60 Hz; shorter presses risk being missed entirely.</summary>
    public TimeSpan MinPressDuration { get; init; } = TimeSpan.FromMilliseconds(20);

    public TimeSpan MaxPressDuration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Safety watchdog for explicit holds: auto-release if no release arrives.</summary>
    public TimeSpan DefaultMaxHold { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxHold { get; init; } = TimeSpan.FromMinutes(5);

    public NeutraliseMode NeutraliseOnConnect { get; init; } = NeutraliseMode.OnlyIfHeld;
}

public sealed record SessionOptions
{
    public SysBotClientOptions Client { get; init; } = new();

    /// <summary>When idle, probe the module this often to detect a dead connection and measure latency.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ReconnectInitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Random spread applied to each backoff delay (0.2 = ±20%).</summary>
    public double ReconnectJitter { get; init; } = 0.2;

    /// <summary>A connection must survive this long before the backoff resets.</summary>
    public TimeSpan StableConnectionThreshold { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an API "connect" call waits for the first attempt to finish.</summary>
    public TimeSpan ConnectWaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public ControllerOptions Controller { get; init; } = new();

    public TimeSpan GetBackoffDelay(int attempt, Random? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        var exponent = Math.Min(attempt - 1, 16);
        var baseMs = Math.Min(ReconnectMaxDelay.TotalMilliseconds, ReconnectInitialDelay.TotalMilliseconds * Math.Pow(2, exponent));
        if (ReconnectJitter > 0)
        {
            var spread = (((random ?? Random.Shared).NextDouble() * 2) - 1) * ReconnectJitter;
            baseMs *= 1 + spread;
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, baseMs));
    }
}
