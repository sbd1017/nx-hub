using NxHub.Core.Events;
using NxHub.Core.Sessions;
using NxHub.Protocol.Transport;
using NxHub.Tests.Shared;

namespace NxHub.Core.Tests;

public class SessionLifecycleTests
{
    [Fact]
    public async Task Enabled_switch_connects_on_start()
    {
        await using var h = new SessionHarness();

        await h.WaitForStateAsync(ConnectionState.Connected);

        var status = h.Status;
        Assert.Equal(1, status.ConnectionCount);
        Assert.Equal("2.5", status.Capabilities?.RawVersion);
        Assert.NotNull(status.ConnectedSince);
        Assert.NotNull(status.Latency);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task Disabled_switch_waits_for_an_explicit_connect()
    {
        await using var h = new SessionHarness(enabled: false);
        await Task.Delay(100);
        Assert.Equal(0, h.Switch.ConnectAttempts);
        Assert.Equal(ConnectionState.Disconnected, h.Session.State);

        var status = await h.Session.ConnectAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.True(status.AutoConnect);
    }

    [Fact]
    public async Task Failed_connections_back_off_exponentially_up_to_the_cap()
    {
        await using var h = new SessionHarness(configure: o => o with { ReconnectMaxDelay = TimeSpan.FromSeconds(4) });
        h.Switch.Online = false;
        var expected = new[] { 1, 2, 4, 4, 4 };

        for (var attempt = 1; attempt <= expected.Length; attempt++)
        {
            var target = attempt;
            await h.AdvanceUntilAsync(
                () => h.Switch.ConnectAttempts == target && h.Status.State == ConnectionState.Reconnecting && h.Status.NextAttemptAt is not null && h.Status.ReconnectAttempt == target,
                $"attempt {target} should fail and schedule a retry");
            var status = h.Status;
            Assert.Equal(TimeSpan.FromSeconds(expected[attempt - 1]), status.NextAttemptAt!.Value - status.LastError!.At);
            Assert.Equal("switch_connection_failed", status.LastError?.Code);
        }

        h.Switch.Online = true;
        await h.AdvanceUntilAsync(() => h.Session.State == ConnectionState.Connected, "should connect once online");
        Assert.Equal(0, h.Status.ReconnectAttempt);
        Assert.Null(h.Status.LastError);
    }

    [Fact]
    public async Task Heartbeat_detects_an_unresponsive_switch_and_reconnects()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);
        h.Switch.Responsive = false;

        await h.AdvanceUntilAsync(() => h.Status.ConnectionLossCount == 1, "heartbeat should time out", TimeSpan.FromSeconds(1));
        Assert.Equal("switch_timeout", h.Status.LastError?.Code);

        h.Switch.Responsive = true;
        await h.AdvanceUntilAsync(() => h.Session.State == ConnectionState.Connected, "should reconnect");
        Assert.Equal(2, h.Status.ConnectionCount);
        Assert.Equal(1, h.Switch.OpenConnections);
    }

    [Fact]
    public async Task Remote_disconnect_is_detected_and_recovered()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);

        h.Switch.DropConnections();

        await h.AdvanceUntilAsync(() => h.Status.ConnectionCount == 2 && h.Session.State == ConnectionState.Connected, "should reconnect after a drop", TimeSpan.FromSeconds(1));
        Assert.Equal(1, h.Status.ConnectionLossCount);
        Assert.Contains(h.Session.Activity.GetRecent(), e => e.Message.StartsWith("Connection lost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disconnect_stops_reconnecting()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);

        var status = await h.Session.DisconnectAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Disconnected, status.State);
        Assert.False(status.AutoConnect);
        var attempts = h.Switch.ConnectAttempts;
        h.Time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(100);
        Assert.Equal(attempts, h.Switch.ConnectAttempts);
        Assert.Equal(0, h.Switch.OpenConnections);
    }

    [Fact]
    public async Task Changing_the_address_reconnects_to_the_new_endpoint()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);

        await h.Session.ApplyDeviceAsync(h.Device with { Host = "192.0.2.99" }, CancellationToken.None);

        await Eventually.TrueAsync(
            () => h.Status.ConnectionCount == 2 && h.Session.State == ConnectionState.Connected,
            "should reconnect without backoff");
        lock (h.Switch.Endpoints)
        {
            Assert.Equal(new SwitchEndpoint("192.0.2.99", 6000), h.Switch.Endpoints[^1]);
        }

        Assert.Equal(0, h.Status.ConnectionLossCount);
    }

    [Fact]
    public async Task Disabling_the_switch_disconnects()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);

        await h.Session.ApplyDeviceAsync(h.Device with { Enabled = false }, CancellationToken.None);

        Assert.Equal(ConnectionState.Disconnected, h.Session.State);
    }

    [Fact]
    public async Task State_changes_are_published_as_events()
    {
        await using var h = new SessionHarness(enabled: false);
        using var subscription = h.Events.Subscribe();

        await h.Session.ConnectAsync(CancellationToken.None);
        await h.Session.DisconnectAsync(CancellationToken.None);

        var states = new List<ConnectionState>();
        while (subscription.Reader.TryRead(out var e))
        {
            if (e.Type == EventTypes.SwitchState && e.Data is SwitchStatus s && (states.Count == 0 || states[^1] != s.State))
            {
                states.Add(s.State);
            }
        }

        // The loop may publish its initial Disconnected state after we subscribed.
        Assert.Equal(
            [ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Disconnecting, ConnectionState.Disconnected],
            states.SkipWhile(s => s == ConnectionState.Disconnected));
    }

    [Fact]
    public async Task Dispose_shuts_down_cleanly()
    {
        var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);

        await h.DisposeAsync();

        Assert.Equal(0, h.Switch.OpenConnections);
        Assert.Equal(ConnectionState.Disconnected, h.Session.State);
    }
}
