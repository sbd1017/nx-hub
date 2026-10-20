using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NxHub.Core.Diagnostics;

public sealed record PingResult(bool Success, double? RoundTripMs, string Message);

/// <summary>Network primitives used by diagnostics. Abstracted so tests need no real network.</summary>
public interface INetworkProbe
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);

    Task<PingResult> PingAsync(string host, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Opens and immediately closes a TCP connection.</summary>
    Task<(bool Success, double ElapsedMs, string Message)> ProbeTcpAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class SystemNetworkProbe : INetworkProbe
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PingResult> PingAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new PingResult(true, reply.RoundtripTime, $"Replied in {reply.RoundtripTime} ms.")
                : new PingResult(false, null, $"No ICMP reply ({reply.Status}).");
        }
        catch (Exception ex) when (ex is PingException or PlatformNotSupportedException or InvalidOperationException or SocketException)
        {
            return new PingResult(false, null, $"ICMP ping unavailable: {ex.GetBaseException().Message}");
        }
    }

    public async Task<(bool Success, double ElapsedMs, string Message)> ProbeTcpAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = TimeProvider.System.GetTimestamp();
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            var elapsed = TimeProvider.System.GetElapsedTime(started).TotalMilliseconds;
            socket.Shutdown(SocketShutdown.Both);
            return (true, elapsed, $"Port {port} accepted a connection.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, timeout.TotalMilliseconds, $"Timed out after {timeout.TotalSeconds:0.#} s connecting to port {port}.");
        }
        catch (SocketException ex)
        {
            var elapsed = TimeProvider.System.GetElapsedTime(started).TotalMilliseconds;
            var hint = ex.SocketErrorCode == SocketError.ConnectionRefused
                ? " The host is reachable but nothing is listening on that port. Check sys-botbase is installed, and note it only starts listening about 20 s after the Switch boots."
                : string.Empty;
            return (false, elapsed, $"Connection failed ({ex.SocketErrorCode}).{hint}");
        }
    }
}
