using NxHub.Core.Errors;
using NxHub.Core.Sessions;
using NxHub.Protocol;

namespace NxHub.Core.Tests;

public class LeaseTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_hand_over_disconnects_and_refuses_input_until_released()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);

        var lease = await h.Session.AcquireLeaseAsync("Trade bot", Ttl, CancellationToken.None);

        Assert.Equal("Trade bot", lease.Holder);
        Assert.Equal(SessionHarness.Start + Ttl, lease.ExpiresAt);
        await h.WaitForStateAsync(ConnectionState.Disconnected);
        Assert.Equal(0, h.Switch.OpenConnections);
        Assert.Equal(lease, h.Status.Lease);

        var press = await Assert.ThrowsAsync<NxHubException>(() => h.Session.Controller.PressAsync(SwitchButton.A, null, CancellationToken.None));
        Assert.Equal("switch_leased", press.Code);
        var connect = await Assert.ThrowsAsync<NxHubException>(() => h.Session.ConnectAsync(CancellationToken.None));
        Assert.Equal("switch_leased", connect.Code);
        Assert.Equal("switch_leased", Assert.Throws<NxHubException>(h.Session.ThrowIfLeased).Code);

        await h.Session.ReleaseLeaseAsync();

        await h.WaitForStateAsync(ConnectionState.Connected);
        Assert.Null(h.Status.Lease);
        await h.Session.Controller.ReleaseAllAsync(everything: true, CancellationToken.None);
    }

    [Fact]
    public async Task Another_program_cannot_take_a_handed_over_switch_but_the_holder_can_renew()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);
        await h.Session.AcquireLeaseAsync("Trade bot", Ttl, CancellationToken.None);

        var other = await Assert.ThrowsAsync<NxHubException>(() => h.Session.AcquireLeaseAsync("Something else", Ttl, CancellationToken.None));
        Assert.Equal("switch_leased", other.Code);

        h.Time.Advance(TimeSpan.FromSeconds(30));
        var renewed = await h.Session.AcquireLeaseAsync("Trade bot", Ttl, CancellationToken.None);

        Assert.Equal(SessionHarness.Start, renewed.AcquiredAt);
        Assert.Equal(SessionHarness.Start + TimeSpan.FromSeconds(90), renewed.ExpiresAt);
    }

    [Fact]
    public async Task An_unrenewed_hand_over_expires_and_nx_hub_reconnects()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);
        await h.Session.AcquireLeaseAsync("Trade bot", Ttl, CancellationToken.None);
        await h.WaitForStateAsync(ConnectionState.Disconnected);

        h.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.NotNull(h.Status.Lease);

        await h.AdvanceUntilAsync(() => h.Session.State == ConnectionState.Connected, "should take the Switch back after the lease expires", TimeSpan.FromSeconds(1));
        Assert.Null(h.Status.Lease);
        Assert.Contains(h.Session.Activity.GetRecent(20), a => a.Message.Contains("stopped renewing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_disconnect_during_a_hand_over_is_remembered_when_it_ends()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);
        await h.Session.AcquireLeaseAsync("Trade bot", Ttl, CancellationToken.None);

        await h.Session.DisconnectAsync(CancellationToken.None);
        await h.Session.ReleaseLeaseAsync();
        await Task.Delay(100);

        Assert.Equal(ConnectionState.Disconnected, h.Session.State);
        Assert.False(h.Status.AutoConnect);
    }

    [Fact]
    public async Task A_switch_that_was_not_connected_stays_disconnected_after_a_hand_over()
    {
        await using var h = new SessionHarness(enabled: false);

        await h.Session.AcquireLeaseAsync("Trade bot", Ttl, CancellationToken.None);
        await h.Session.ReleaseLeaseAsync();
        await Task.Delay(100);

        Assert.Equal(0, h.Switch.ConnectAttempts);
        Assert.Equal(ConnectionState.Disconnected, h.Session.State);
    }
}
