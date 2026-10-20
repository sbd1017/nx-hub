using NxHub.Protocol;
using NxHub.Protocol.SysBotBase;
using NxHub.Protocol.Transport;
using NxHub.Tests.Shared;

namespace NxHub.Protocol.Tests;

public class SysBotBaseClientTests
{
    private static readonly SysBotClientOptions FastOptions = new()
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(200),
        ConnectTimeout = TimeSpan.FromMilliseconds(500),
    };

    [Fact]
    public async Task Handshake_disables_echo_then_reads_version()
    {
        var fake = new FakeSwitch { Version = "2.5" };
        await using var client = new SysBotBaseClient(fake.Create(new SwitchEndpoint("sw", 6000)), FastOptions);

        var caps = await client.ConnectAsync(CancellationToken.None);

        Assert.Equal(["configure echoCommands 0", "getVersion"], fake.Sent);
        Assert.Equal(SysBotImplementation.SysBotBase, caps.Implementation);
        Assert.True(client.IsConnected);
        Assert.NotNull(client.HandshakeRoundTrip);
    }

    [Fact]
    public async Task Echo_disable_can_be_turned_off()
    {
        var fake = new FakeSwitch();
        await using var client = new SysBotBaseClient(fake.Create(new SwitchEndpoint("sw", 6000)), FastOptions with { DisableEchoOnConnect = false });

        await client.ConnectAsync(CancellationToken.None);

        Assert.Equal(["getVersion"], fake.Sent);
    }

    [Fact]
    public async Task Refused_connection_throws_connection_exception()
    {
        var fake = new FakeSwitch { Online = false };
        await using var client = new SysBotBaseClient(fake.Create(new SwitchEndpoint("sw", 6000)), FastOptions);

        await Assert.ThrowsAsync<SysBotConnectionException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Missing_response_times_out_and_faults_the_client()
    {
        var fake = new FakeSwitch();
        await using var client = new SysBotBaseClient(fake.Create(new SwitchEndpoint("sw", 6000)), FastOptions);
        await client.ConnectAsync(CancellationToken.None);
        fake.Responsive = false;

        await Assert.ThrowsAsync<SysBotTimeoutException>(() => client.QueryAsync(SysBotCommands.GetVersion(), CancellationToken.None));

        Assert.True(client.IsFaulted);
        Assert.False(client.IsConnected);
        // A late reply could now be mistaken for the next answer, so the client refuses further use.
        await Assert.ThrowsAsync<SysBotConnectionException>(() => client.SendAsync(SysBotCommands.Press(SwitchButton.A), CancellationToken.None));
    }

    [Fact]
    public async Task Remote_close_surfaces_as_connection_exception()
    {
        var fake = new FakeSwitch();
        await using var client = new SysBotBaseClient(fake.Create(new SwitchEndpoint("sw", 6000)), FastOptions);
        await client.ConnectAsync(CancellationToken.None);
        fake.Responsive = false;
        fake.DropConnections();

        await Assert.ThrowsAsync<SysBotConnectionException>(() => client.QueryAsync(SysBotCommands.GetVersion(), CancellationToken.None));
        Assert.True(client.IsFaulted);
    }

    [Fact]
    public async Task Send_and_query_reject_mismatched_command_kinds()
    {
        var fake = new FakeSwitch();
        await using var client = new SysBotBaseClient(fake.Create(new SwitchEndpoint("sw", 6000)), FastOptions);
        await client.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(SysBotCommands.GetVersion(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.QueryAsync(SysBotCommands.Press(SwitchButton.A), CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_queries_are_serialised_and_paired_correctly()
    {
        var fake = new FakeSwitch { Version = "2.5", TitleIdResponse = "0100000000000042" };
        await using var client = new SysBotBaseClient(fake.Create(new SwitchEndpoint("sw", 6000)), FastOptions);
        await client.ConnectAsync(CancellationToken.None);

        var tasks = Enumerable.Range(0, 50)
            .Select(i => i % 2 == 0
                ? client.QueryAsync(SysBotCommands.GetVersion(), CancellationToken.None).ContinueWith(t => (Expected: "2.5", Actual: t.Result.Line), TaskScheduler.Default)
                : client.QueryAsync(SysBotCommands.GetTitleId(), CancellationToken.None).ContinueWith(t => (Expected: "0100000000000042", Actual: t.Result.Line), TaskScheduler.Default))
            .ToArray();

        foreach (var (expected, actual) in await Task.WhenAll(tasks))
        {
            Assert.Equal(expected, actual);
        }
    }
}
