using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using NxHub.Core;
using NxHub.Core.Events;
using NxHub.Server.Auth;

namespace NxHub.Server.Api;

/// <summary>
/// <c>GET /api/events</c> WebSocket. Browsers cannot set headers on WebSockets and tokens do not
/// belong in URLs, so when auth is on the first client message must be
/// <c>{"type":"auth","token":"…"}</c> within 5 seconds. The server then sends <c>hello</c>, a
/// <c>switch.state</c> snapshot per Switch, and live events. Optional <c>?switchId=</c> filter.
/// </summary>
public static class EventsEndpoint
{
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(5);

    public static void MapNxHubEvents(this IEndpointRouteBuilder app)
    {
        app.Map("/api/events", HandleAsync).ExcludeFromDescription();
    }

    private static async Task HandleAsync(
        HttpContext context,
        EventHub hub,
        SwitchManager manager,
        AccessTokenService auth,
        IOptions<JsonOptions> json,
        TimeProvider time)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Expected a WebSocket upgrade.");
            return;
        }

        // A proxy-authenticated (cookie) session must not be usable from other websites.
        var trustedProxy = auth.IsTrustedProxyUser(context);
        if (trustedProxy && !AccessTokenService.IsSameOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var filter = context.Request.Query["switchId"].FirstOrDefault();
        var serializer = json.Value.SerializerOptions;
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var aborted = context.RequestAborted;

        if (auth.Enabled && !trustedProxy && !await AuthenticateAsync(socket, auth, aborted))
        {
            await CloseQuietlyAsync(socket, WebSocketCloseStatus.PolicyViolation, "Authentication required.");
            return;
        }

        using var subscription = hub.Subscribe();
        await SendAsync(socket, new { type = "hello", server = "NX Hub", version = Endpoints.Version }, serializer, aborted);
        foreach (var session in manager.Sessions.Where(s => filter is null || s.SwitchId == filter))
        {
            await SendAsync(socket, new NxHubEvent(EventTypes.SwitchState, session.SwitchId, time.GetUtcNow(), session.GetStatus()), serializer, aborted);
            if (session.Macros.State is { } run)
            {
                await SendAsync(socket, new NxHubEvent(EventTypes.MacroState, session.SwitchId, time.GetUtcNow(), run), serializer, aborted);
            }
        }

        using var closed = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var receiveLoop = DrainIncomingAsync(socket, closed);
        try
        {
            await foreach (var hubEvent in subscription.Reader.ReadAllAsync(closed.Token))
            {
                if (filter is not null && hubEvent.SwitchId is not null && hubEvent.SwitchId != filter)
                {
                    continue;
                }

                await SendAsync(socket, hubEvent, serializer, closed.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Client disconnected.
        }

        await closed.CancelAsync();
        await receiveLoop;
        await CloseQuietlyAsync(socket, WebSocketCloseStatus.NormalClosure, "Bye.");
    }

    private static async Task<bool> AuthenticateAsync(WebSocket socket, AccessTokenService auth, CancellationToken aborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        timeout.CancelAfter(AuthTimeout);
        try
        {
            var buffer = new byte[1024];
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage)
            {
                return false;
            }

            using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "auth"
                && doc.RootElement.TryGetProperty("token", out var token) && auth.Validate(token.GetString());
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException or WebSocketException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Reads (and ignores) client messages so close frames and pings are processed.</summary>
    private static async Task DrainIncomingAsync(WebSocket socket, CancellationTokenSource closed)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open && !closed.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, closed.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            await closed.CancelAsync();
        }
    }

    private static Task SendAsync<T>(WebSocket socket, T payload, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, options);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(status, reason, timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
            }
        }
    }
}
