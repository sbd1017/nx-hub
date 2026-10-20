using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NxHub.Server.Tests;

public class EventsTests
{
    private static async Task<WebSocket> ConnectAsync(ServerFixture server)
    {
        var client = server.Factory.Server.CreateWebSocketClient();
        return await client.ConnectAsync(new Uri(server.Factory.Server.BaseAddress, "/api/events"), CancellationToken.None);
    }

    private static Task SendAsync(WebSocket socket, object payload) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement?> ReceiveAsync(WebSocket socket, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        var buffer = new byte[64 * 1024];
        var builder = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(builder.ToString()).RootElement.Clone();
    }

    [Fact]
    public async Task Authenticated_socket_receives_hello_snapshot_and_live_events()
    {
        await using var server = new ServerFixture();
        var id = await server.AddSimulatorAsync();
        using var socket = await ConnectAsync(server);

        await SendAsync(socket, new { type = "auth", token = ServerFixture.Token });
        var hello = await ReceiveAsync(socket);
        var snapshot = await ReceiveAsync(socket);

        Assert.Equal("hello", hello?.GetProperty("type").GetString());
        Assert.Equal("switch.state", snapshot?.GetProperty("type").GetString());
        Assert.Equal(id, snapshot?.GetProperty("switchId").GetString());
        Assert.Equal("connected", snapshot?.GetProperty("data").GetProperty("state").GetString());

        await server.Client.PostAsync($"/api/switches/{id}/disconnect", null);
        var sawDisconnected = false;
        for (var i = 0; i < 20 && !sawDisconnected; i++)
        {
            var e = await ReceiveAsync(socket);
            sawDisconnected = e?.GetProperty("type").GetString() == "switch.state"
                && e.Value.GetProperty("data").GetProperty("state").GetString() == "disconnected";
        }

        Assert.True(sawDisconnected, "should receive the disconnected state event");
    }

    [Fact]
    public async Task Socket_without_valid_auth_is_closed()
    {
        await using var server = new ServerFixture();
        using var socket = await ConnectAsync(server);

        await SendAsync(socket, new { type = "auth", token = "wrong-token-value-xx" });
        var message = await ReceiveAsync(socket);

        Assert.Null(message);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }
}
