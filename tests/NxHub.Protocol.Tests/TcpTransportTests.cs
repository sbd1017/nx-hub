using System.Net;
using System.Net.Sockets;
using System.Text;
using NxHub.Protocol;
using NxHub.Protocol.SysBotBase;
using NxHub.Protocol.Transport;
using NxHub.Simulator;
using NxHub.Tests.Shared;

namespace NxHub.Protocol.Tests;

/// <summary>Real loopback sockets against the simulator and hand-rolled servers.</summary>
public class TcpTransportTests
{
    [Theory]
    [InlineData(SimulatorFlavour.SysBotBase, "2.5")]
    [InlineData(SimulatorFlavour.SysBotBaseCpp, "3.33")]
    public async Task Client_talks_to_both_simulator_flavours(SimulatorFlavour flavour, string version)
    {
        await using var simulator = new SysBotSimulator(new SimulatorOptions { Flavour = flavour });
        simulator.Start();
        await using var client = new SysBotBaseClient(new TcpSysBotTransport(new SwitchEndpoint("127.0.0.1", simulator.Port)));

        var caps = await client.ConnectAsync(CancellationToken.None);
        await client.SendAsync(SysBotCommands.Press(SwitchButton.B), CancellationToken.None);
        var title = await client.QueryAsync(SysBotCommands.GetTitleId(), CancellationToken.None);

        Assert.Equal(version, caps.RawVersion);
        Assert.Equal("0100ABCD12340000", title.Line);
        await Eventually.TrueAsync(() => simulator.HeldButtons.Contains("B"), "simulator should see B held");
    }

    [Fact]
    public async Task Lines_split_across_packets_and_crlf_are_reassembled()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            var buffer = new byte[256];
            await socket.ReceiveAsync(buffer);
            foreach (var piece in new[] { "3.", "33\r", "\nnext\n" })
            {
                await socket.SendAsync(Encoding.ASCII.GetBytes(piece));
                await Task.Delay(20);
            }

            await Task.Delay(200);
        });

        await using var transport = new TcpSysBotTransport(new SwitchEndpoint("127.0.0.1", port));
        await transport.ConnectAsync(CancellationToken.None);
        await transport.SendAsync(SysBotCommands.GetVersion().Encode(), CancellationToken.None);

        Assert.Equal("3.33", await transport.ReadLineAsync(1024, CancellationToken.None));
        Assert.Equal("next", await transport.ReadLineAsync(1024, CancellationToken.None));
        await server;
    }

    [Fact]
    public async Task Overlong_response_is_a_protocol_error()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            await socket.SendAsync(Encoding.ASCII.GetBytes(new string('F', 5000) + "\n"));
            await Task.Delay(200);
        });

        await using var transport = new TcpSysBotTransport(new SwitchEndpoint("127.0.0.1", port));
        await transport.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<SysBotProtocolException>(() => transport.ReadLineAsync(1024, CancellationToken.None));
        await server;
    }

    [Fact]
    public async Task Refused_port_reports_connection_exception()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await using var transport = new TcpSysBotTransport(new SwitchEndpoint("127.0.0.1", port));
        var error = await Assert.ThrowsAsync<SysBotConnectionException>(() => transport.ConnectAsync(CancellationToken.None));
        Assert.Contains("ConnectionRefused", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Peer_close_is_detected_on_read()
    {
        await using var simulator = new SysBotSimulator();
        simulator.Start();
        await using var transport = new TcpSysBotTransport(new SwitchEndpoint("127.0.0.1", simulator.Port));
        await transport.ConnectAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => simulator.ConnectionCount == 1, "simulator should accept");

        simulator.DisconnectAllClients();

        await Assert.ThrowsAsync<SysBotConnectionException>(() => transport.ReadLineAsync(1024, CancellationToken.None));
    }

    [Fact]
    public async Task Hung_module_times_out_via_client()
    {
        await using var simulator = new SysBotSimulator();
        simulator.Start();
        await using var client = new SysBotBaseClient(
            new TcpSysBotTransport(new SwitchEndpoint("127.0.0.1", simulator.Port)),
            new SysBotClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(200) });
        await client.ConnectAsync(CancellationToken.None);
        simulator.Muted = true;

        await Assert.ThrowsAsync<SysBotTimeoutException>(() => client.QueryAsync(SysBotCommands.GetVersion(), CancellationToken.None));
    }
}
