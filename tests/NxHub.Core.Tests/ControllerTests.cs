using NxHub.Core.Errors;
using NxHub.Core.Sessions;
using NxHub.Protocol;
using NxHub.Tests.Shared;

namespace NxHub.Core.Tests;

public class ControllerTests
{
    private static async Task<SessionHarness> ConnectedAsync(Func<SessionOptions, SessionOptions>? configure = null)
    {
        var h = new SessionHarness(configure: configure);
        await h.WaitForStateAsync(ConnectionState.Connected);
        h.Switch.ClearSent();
        return h;
    }

    [Fact]
    public async Task Press_holds_for_the_duration_then_releases()
    {
        await using var h = await ConnectedAsync();

        var press = h.Session.Controller.PressAsync(SwitchButton.A, TimeSpan.FromMilliseconds(150), CancellationToken.None);
        await Eventually.TrueAsync(() => h.Switch.Sent.Contains("press A"), "press should be sent");
        Assert.DoesNotContain("release A", h.Switch.Sent);
        Assert.Contains(SwitchButton.A, h.Session.Controller.GetSnapshot().HeldButtons);

        h.Time.Advance(TimeSpan.FromMilliseconds(150));
        await press;

        Assert.Equal(["press A", "release A"], h.Switch.Sent);
        Assert.Empty(h.Session.Controller.GetSnapshot().HeldButtons);
    }

    [Fact]
    public async Task Cancelled_press_still_releases()
    {
        await using var h = await ConnectedAsync();
        using var cts = new CancellationTokenSource();

        var press = h.Session.Controller.PressAsync(SwitchButton.B, TimeSpan.FromSeconds(5), cts.Token);
        await Eventually.TrueAsync(() => h.Switch.Sent.Contains("press B"), "press should be sent");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => press);
        Assert.Contains("release B", h.Switch.Sent);
        Assert.Empty(h.Session.Controller.GetSnapshot().HeldButtons);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(60_000)]
    public async Task Press_duration_is_validated(int milliseconds)
    {
        await using var h = await ConnectedAsync();

        var error = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            h.Session.Controller.PressAsync(SwitchButton.A, TimeSpan.FromMilliseconds(milliseconds), CancellationToken.None));

        Assert.True(error.Errors.ContainsKey("durationMs"));
        Assert.Empty(h.Switch.Sent);
    }

    [Fact]
    public async Task Hold_and_release()
    {
        await using var h = await ConnectedAsync();

        await h.Session.Controller.HoldAsync(SwitchButton.ZR, null, CancellationToken.None);
        Assert.Equal([SwitchButton.ZR], h.Session.Controller.GetSnapshot().HeldButtons);

        await h.Session.Controller.ReleaseAsync(SwitchButton.ZR, CancellationToken.None);
        Assert.Equal(["press ZR", "release ZR"], h.Switch.Sent);
        Assert.Empty(h.Session.Controller.GetSnapshot().HeldButtons);
    }

    [Fact]
    public async Task Refreshing_a_hold_is_silent_and_release_reports_duration()
    {
        await using var h = await ConnectedAsync();
        using var events = h.Events.Subscribe();

        await h.Session.Controller.HoldAsync(SwitchButton.Y, TimeSpan.FromSeconds(10), CancellationToken.None);
        h.Time.Advance(TimeSpan.FromSeconds(4));
        await h.Session.Controller.HoldAsync(SwitchButton.Y, TimeSpan.FromSeconds(10), CancellationToken.None);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.Session.Controller.ReleaseAsync(SwitchButton.Y, CancellationToken.None);

        var messages = h.Session.Activity.GetRecent().Select(e => e.Message).ToArray();
        Assert.Single(messages, m => m.StartsWith("Holding Y", StringComparison.Ordinal));
        Assert.Contains("Released Y (held 5000 ms).", messages);

        var controllerEvents = new List<ControllerSnapshot>();
        while (events.Reader.TryRead(out var e))
        {
            if (e.Type == Events.EventTypes.ControllerState)
            {
                controllerEvents.Add((ControllerSnapshot)e.Data!);
            }
        }

        // One event when Y became held, one when it was released; refreshes do not spam.
        Assert.Equal(2, controllerEvents.Count);
        Assert.Equal([SwitchButton.Y], controllerEvents[0].HeldButtons);
        Assert.Empty(controllerEvents[1].HeldButtons);
    }

    [Fact]
    public async Task Hold_watchdog_auto_releases()
    {
        await using var h = await ConnectedAsync();
        await h.Session.Controller.HoldAsync(SwitchButton.L, TimeSpan.FromSeconds(2), CancellationToken.None);

        h.Time.Advance(TimeSpan.FromSeconds(2));

        await Eventually.TrueAsync(() => h.Switch.Sent.Contains("release L"), "watchdog should release L");
        await Eventually.TrueAsync(() => h.Session.Controller.GetSnapshot().HeldButtons.Count == 0, "L should no longer be tracked");
        Assert.Contains(h.Session.Activity.GetRecent(), e => e.Message.Contains("safety watchdog", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Release_before_watchdog_cancels_it()
    {
        await using var h = await ConnectedAsync();
        await h.Session.Controller.HoldAsync(SwitchButton.L, TimeSpan.FromSeconds(2), CancellationToken.None);
        await h.Session.Controller.ReleaseAsync(SwitchButton.L, CancellationToken.None);

        h.Time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Equal(1, h.Switch.CountSent("release L"));
    }

    [Fact]
    public async Task Inputs_held_when_the_connection_drops_are_released_on_reconnect()
    {
        await using var h = await ConnectedAsync();
        await h.Session.Controller.HoldAsync(SwitchButton.ZL, TimeSpan.FromSeconds(60), CancellationToken.None);
        await h.Session.Controller.SetStickAsync(SwitchStick.Left, new StickPosition(1000, 2000), null, TimeSpan.FromSeconds(60), CancellationToken.None);

        h.Switch.DropConnections();
        await h.AdvanceUntilAsync(() => h.Status.ConnectionLossCount == 1, "loss should be detected", TimeSpan.FromSeconds(1));
        Assert.True(h.Session.Controller.GetSnapshot().PendingNeutralise);
        h.Switch.ClearSent();

        await h.AdvanceUntilAsync(() => h.Session.State == ConnectionState.Connected, "should reconnect");
        await Eventually.TrueAsync(() => h.Switch.Sent.Contains("release ZL"), "ZL should be released after reconnecting");

        Assert.Contains("setStick LEFT 0 0", h.Switch.Sent);
        var snapshot = h.Session.Controller.GetSnapshot();
        Assert.Empty(snapshot.HeldButtons);
        Assert.Empty(snapshot.Sticks);
        Assert.False(snapshot.PendingNeutralise);
    }

    [Fact]
    public async Task Neutralise_always_mode_releases_everything_on_connect()
    {
        await using var h = new SessionHarness(configure: o => o with { Controller = o.Controller with { NeutraliseOnConnect = NeutraliseMode.Always } });
        await h.WaitForStateAsync(ConnectionState.Connected);

        await Eventually.TrueAsync(() => h.Switch.Sent.Count(s => s.StartsWith("release ", StringComparison.Ordinal)) == Enum.GetValues<SwitchButton>().Length, "all buttons released");
        Assert.Contains("setStick RIGHT 0 0", h.Switch.Sent);
    }

    [Fact]
    public async Task Disconnect_releases_held_inputs_first()
    {
        await using var h = await ConnectedAsync();
        await h.Session.Controller.HoldAsync(SwitchButton.A, null, CancellationToken.None);

        await h.Session.DisconnectAsync(CancellationToken.None);

        Assert.Equal(["press A", "release A"], h.Switch.Sent);
        Assert.Empty(h.Session.Controller.GetSnapshot().HeldButtons);
    }

    [Fact]
    public async Task Commands_fail_cleanly_when_not_connected()
    {
        await using var h = new SessionHarness(enabled: false);

        var error = await Assert.ThrowsAsync<NxHubException>(() => h.Session.Controller.HoldAsync(SwitchButton.A, null, CancellationToken.None));

        Assert.Equal(ErrorKind.NotConnected, error.Kind);
        Assert.Equal("switch_not_connected", error.Code);
        Assert.Empty(h.Session.Controller.GetSnapshot().HeldButtons);
    }

    [Fact]
    public async Task Stick_with_duration_returns_to_centre()
    {
        await using var h = await ConnectedAsync();

        var move = h.Session.Controller.SetStickAsync(SwitchStick.Right, StickPosition.FromNormalised(0, 1), TimeSpan.FromMilliseconds(300), null, CancellationToken.None);
        await Eventually.TrueAsync(() => h.Switch.Sent.Contains("setStick RIGHT 0 32767"), "stick should move");
        h.Time.Advance(TimeSpan.FromMilliseconds(300));
        await move;

        Assert.Equal(["setStick RIGHT 0 32767", "setStick RIGHT 0 0"], h.Switch.Sent);
        Assert.Empty(h.Session.Controller.GetSnapshot().Sticks);
    }

    [Fact]
    public async Task Stick_watchdog_recentres()
    {
        await using var h = await ConnectedAsync();
        await h.Session.Controller.SetStickAsync(SwitchStick.Left, StickPosition.FromNormalised(-1, 0), null, TimeSpan.FromSeconds(1), CancellationToken.None);

        h.Time.Advance(TimeSpan.FromSeconds(1));

        await Eventually.TrueAsync(() => h.Switch.Sent.Contains("setStick LEFT 0 0"), "watchdog should centre the stick");
    }

    [Fact]
    public async Task Release_all_everything_covers_every_input()
    {
        await using var h = await ConnectedAsync();

        await h.Session.Controller.ReleaseAllAsync(everything: true, CancellationToken.None);

        Assert.Equal(Enum.GetValues<SwitchButton>().Length + 2, h.Switch.Sent.Count);
    }

    [Fact]
    public async Task Reset_releases_then_detaches()
    {
        await using var h = await ConnectedAsync();
        await h.Session.Controller.HoldAsync(SwitchButton.X, null, CancellationToken.None);

        await h.Session.Controller.ResetAsync(CancellationToken.None);

        Assert.Equal("detachController", h.Switch.Sent[^1]);
        Assert.Contains("release X", h.Switch.Sent);
        Assert.Empty(h.Session.Controller.GetSnapshot().HeldButtons);
    }

    [Fact]
    public async Task Timeout_during_a_command_triggers_reconnect()
    {
        await using var h = await ConnectedAsync();
        h.Switch.Responsive = false;

        var error = await Assert.ThrowsAsync<NxHubException>(() => h.Session.QueryAsync(Protocol.SysBotBase.SysBotCommands.GetVersion(), CancellationToken.None));

        Assert.Equal(ErrorKind.SwitchTimeout, error.Kind);
        await Eventually.TrueAsync(() => h.Status.ConnectionLossCount == 1, "loss should be recorded");
        h.Switch.Responsive = true;
        await h.AdvanceUntilAsync(() => h.Session.State == ConnectionState.Connected, "should reconnect");
    }
}
