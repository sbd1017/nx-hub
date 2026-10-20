using NxHub.Core.Errors;
using NxHub.Core.Sessions;
using NxHub.Tests.Shared;

namespace NxHub.Core.Tests;

public class ScreenTests
{
    [Fact]
    public async Task Captures_and_decodes_a_frame()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);

        var frame = await h.Session.Screen.GetFrameAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0], frame.Jpeg[..4]);
        Assert.Equal(1, frame.Sequence);
    }

    [Fact]
    public async Task Recent_frames_are_reused_and_concurrent_requests_share_one_capture()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);

        var frames = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => h.Session.Screen.GetFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None)));
        var again = await h.Session.Screen.GetFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.All(frames, f => Assert.Equal(1, f.Sequence));
        Assert.Same(frames[0], again);
        Assert.Equal(1, h.Switch.ScreenRequests);
    }

    [Fact]
    public async Task Empty_response_means_screenshots_are_unavailable()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);
        h.Switch.ScreenResponse = string.Empty;

        var error = await Assert.ThrowsAsync<NxHubException>(() => h.Session.Screen.GetFrameAsync(TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("screenshot_unavailable", error.Code);
        Assert.Equal(ConnectionState.Connected, h.Session.State);
    }

    [Fact]
    public async Task A_viewer_cancelling_does_not_break_the_connection()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Session.Screen.GetFrameAsync(TimeSpan.Zero, cts.Token));
        await Eventually.TrueAsync(() => h.Session.Screen.Latest is not null, "the shared capture should still complete");

        Assert.Equal(ConnectionState.Connected, h.Session.State);
        Assert.Equal(0, h.Status.ConnectionLossCount);
    }
}
