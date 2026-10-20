using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NxHub.Core.Diagnostics;
using NxHub.Core.Sessions;

namespace NxHub.Core.Tests;

public class DiagnosticsTests
{
    private sealed class FakeNetwork : INetworkProbe
    {
        public bool Resolves { get; set; } = true;

        public bool Pings { get; set; } = true;

        public bool TcpOpen { get; set; } = true;

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(Resolves ? [IPAddress.Parse("192.0.2.10")] : []);

        public Task<PingResult> PingAsync(string host, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(Pings ? new PingResult(true, 3, "Replied in 3 ms.") : new PingResult(false, null, "No ICMP reply (TimedOut)."));

        public Task<(bool Success, double ElapsedMs, string Message)> ProbeTcpAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(TcpOpen ? (true, 2.0, "Port accepted a connection.") : (false, 1.0, "Connection failed (ConnectionRefused)."));
    }

    private static DiagnosticsRunner Runner(SessionHarness h, FakeNetwork network) =>
        new(network, h.Switch, h.Options, h.Events, TimeProvider.System, NullLogger<DiagnosticsRunner>.Instance);

    [Fact]
    public async Task Connected_switch_is_ready()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);

        var report = await Runner(h, new FakeNetwork()).RunAsync(h.Session, new DiagnosticOptions(), CancellationToken.None);

        Assert.Equal(OverallStatus.Ready, report.Overall);
        Assert.Equal(
            ["config", "dns", "icmp", "tcp", "sysbotbase", "capabilities", "latency", "title", "controller", "session"],
            report.Checks.Select(c => c.Id));
        Assert.Equal(CheckStatus.Skipped, report.Checks.Single(c => c.Id == "controller").Status);
        Assert.Contains("0100ABCD12340000", report.Checks.Single(c => c.Id == "title").Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Switch.Sent, s => s.StartsWith("press", StringComparison.Ordinal) || s.StartsWith("release", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disconnected_switch_is_checked_with_a_temporary_connection()
    {
        await using var h = new SessionHarness(enabled: false);

        var report = await Runner(h, new FakeNetwork()).RunAsync(h.Session, new DiagnosticOptions(), CancellationToken.None);

        Assert.Equal(OverallStatus.Ready, report.Overall);
        Assert.Equal(1, h.Switch.ConnectAttempts);
        Assert.Equal(0, h.Switch.OpenConnections);
        Assert.Equal(ConnectionState.Disconnected, h.Session.State);
    }

    [Fact]
    public async Task Closed_port_is_not_ready_and_dependent_checks_are_skipped()
    {
        await using var h = new SessionHarness(enabled: false);

        var report = await Runner(h, new FakeNetwork { TcpOpen = false }).RunAsync(h.Session, new DiagnosticOptions(), CancellationToken.None);

        Assert.Equal(OverallStatus.NotReady, report.Overall);
        Assert.Equal(CheckStatus.Fail, report.Checks.Single(c => c.Id == "tcp").Status);
        Assert.All(
            report.Checks.Where(c => c.Id is "sysbotbase" or "latency" or "title"),
            c => Assert.Equal(CheckStatus.Skipped, c.Status));
    }

    [Fact]
    public async Task Silent_port_fails_the_protocol_check()
    {
        await using var h = new SessionHarness(enabled: false);
        h.Switch.Responsive = false;

        var report = await Runner(h, new FakeNetwork()).RunAsync(h.Session, new DiagnosticOptions(), CancellationToken.None);

        Assert.Equal(OverallStatus.NotReady, report.Overall);
        Assert.Equal(CheckStatus.Fail, report.Checks.Single(c => c.Id == "sysbotbase").Status);
    }

    [Fact]
    public async Task Blocked_ping_is_only_a_warning()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);

        var report = await Runner(h, new FakeNetwork { Pings = false }).RunAsync(h.Session, new DiagnosticOptions(), CancellationToken.None);

        Assert.Equal(OverallStatus.Degraded, report.Overall);
        Assert.Equal(CheckStatus.Warn, report.Checks.Single(c => c.Id == "icmp").Status);
    }

    [Fact]
    public async Task High_latency_warns()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);

        var report = await Runner(h, new FakeNetwork()).RunAsync(h.Session, new DiagnosticOptions { LatencyWarnMs = -1 }, CancellationToken.None);

        Assert.Equal(CheckStatus.Warn, report.Checks.Single(c => c.Id == "latency").Status);
    }

    [Fact]
    public async Task Controller_test_is_opt_in_and_sends_a_no_op()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);
        h.Switch.ClearSent();

        var report = await Runner(h, new FakeNetwork()).RunAsync(h.Session, new DiagnosticOptions { IncludeControllerTest = true }, CancellationToken.None);

        Assert.Equal(CheckStatus.Pass, report.Checks.Single(c => c.Id == "controller").Status);
        Assert.Contains("release A", h.Switch.Sent);
        Assert.DoesNotContain(h.Switch.Sent, s => s.StartsWith("press", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unresolvable_hostname_fails_fast()
    {
        await using var h = new SessionHarness(enabled: false);
        await h.Session.ApplyDeviceAsync(h.Device with { Host = "switch.invalid", Enabled = false }, CancellationToken.None);

        var report = await Runner(h, new FakeNetwork { Resolves = false }).RunAsync(h.Session, new DiagnosticOptions(), CancellationToken.None);

        Assert.Equal(CheckStatus.Fail, report.Checks.Single(c => c.Id == "dns").Status);
        Assert.Equal(CheckStatus.Skipped, report.Checks.Single(c => c.Id == "tcp").Status);
        Assert.Equal(0, h.Switch.ConnectAttempts);
    }

    [Fact]
    public async Task Latest_report_is_kept()
    {
        await using var h = new SessionHarness();
        await h.WaitForStateAsync(ConnectionState.Connected);
        var runner = Runner(h, new FakeNetwork());

        Assert.Null(runner.GetLatest(h.Session.SwitchId));
        var report = await runner.RunAsync(h.Session, new DiagnosticOptions(), CancellationToken.None);

        Assert.Same(report, runner.GetLatest(h.Session.SwitchId));
    }
}
