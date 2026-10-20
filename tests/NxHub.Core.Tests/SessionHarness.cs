using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NxHub.Core.Devices;
using NxHub.Core.Events;
using NxHub.Core.Sessions;
using NxHub.Protocol.SysBotBase;
using NxHub.Tests.Shared;

namespace NxHub.Core.Tests;

/// <summary>A session wired to a <see cref="FakeSwitch"/> and a controllable clock.</summary>
internal sealed class SessionHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public SessionHarness(bool enabled = true, Func<SessionOptions, SessionOptions>? configure = null)
    {
        var options = new SessionOptions
        {
            Client = new SysBotClientOptions
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(500),
                ResponseTimeout = TimeSpan.FromMilliseconds(200),
            },
            HeartbeatInterval = TimeSpan.FromSeconds(10),
            ReconnectInitialDelay = TimeSpan.FromSeconds(1),
            ReconnectMaxDelay = TimeSpan.FromSeconds(8),
            ReconnectJitter = 0,
        };
        Options = configure?.Invoke(options) ?? options;
        Device = new SwitchDevice
        {
            Id = "sw_test",
            Name = "Test Switch",
            Host = "192.0.2.10",
            Port = 6000,
            Enabled = enabled,
            CreatedAt = Start,
            UpdatedAt = Start,
        };
        Session = new SwitchSession(Device, Switch, Options, Events, NullLoggerFactory.Instance, Time);
        Session.Start();
    }

    public FakeSwitch Switch { get; } = new();

    public FakeTimeProvider Time { get; } = new(Start);

    public EventHub Events { get; } = new();

    public SessionOptions Options { get; }

    public SwitchDevice Device { get; }

    public SwitchSession Session { get; }

    public SwitchStatus Status => Session.GetStatus();

    public Task WaitForStateAsync(ConnectionState state) =>
        Eventually.TrueAsync(() => Session.State == state, $"session should reach {state} (is {Session.State})");

    /// <summary>Advances fake time in steps until the condition holds (or real time runs out).</summary>
    public async Task AdvanceUntilAsync(Func<bool> condition, string because, TimeSpan? step = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met: {because}. State {Session.State}, attempts {Switch.ConnectAttempts}.");
            }

            Time.Advance(step ?? TimeSpan.FromMilliseconds(250));
            await Task.Delay(5);
        }
    }

    public async ValueTask DisposeAsync() => await Session.DisposeAsync();
}
