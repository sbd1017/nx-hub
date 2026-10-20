using System.Net.Sockets;
using System.Text;
using NxHub.Protocol.SysBotBase;

namespace NxHub.Protocol.Transport;

/// <summary>TCP transport. sys-botbase listens on port 6000 on all interfaces of the Switch.</summary>
public sealed class TcpSysBotTransport : ISysBotTransport
{
    private readonly SwitchEndpoint _endpoint;
    private readonly byte[] _readBuffer = new byte[16 * 1024];
    private Socket? _socket;
    private int _readStart;
    private int _readEnd;
    private bool _disposed;

    public TcpSysBotTransport(SwitchEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public bool IsConnected => _socket is { Connected: true } && !_disposed;

    public string Description => $"tcp://{_endpoint}";

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_socket is not null)
        {
            throw new InvalidOperationException("Transport is already connected; create a new transport to reconnect.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            // Controller commands are tiny; never let Nagle delay a button release.
            NoDelay = true,
        };

        try
        {
            ConfigureKeepAlive(socket);
            await socket.ConnectAsync(_endpoint.Host, _endpoint.Port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            socket.Dispose();
            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new SysBotConnectionException($"Could not connect to {Description}: {((SocketException)ex).SocketErrorCode}.", ex);
        }

        _socket = socket;
        _readStart = _readEnd = 0;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var socket = RequireSocket();
        try
        {
            while (!data.IsEmpty)
            {
                var sent = await socket.SendAsync(data, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (sent <= 0)
                {
                    throw new SysBotConnectionException($"Connection to {Description} was closed while sending.");
                }

                data = data[sent..];
            }
        }
        catch (SocketException ex)
        {
            throw new SysBotConnectionException($"Send to {Description} failed: {ex.SocketErrorCode}.", ex);
        }
        catch (ObjectDisposedException ex)
        {
            // Closed concurrently (disconnect or reconnect) while this send was in flight.
            throw new SysBotConnectionException($"Connection to {Description} was closed.", ex);
        }
    }

    public async Task<string> ReadLineAsync(int maxLength, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        var socket = RequireSocket();
        var line = new StringBuilder();

        while (true)
        {
            // Consume buffered bytes first.
            for (var i = _readStart; i < _readEnd; i++)
            {
                if (_readBuffer[i] != (byte)'\n')
                {
                    continue;
                }

                AppendAscii(line, _readBuffer.AsSpan(_readStart, i - _readStart), maxLength);
                _readStart = i + 1;
                if (line.Length > 0 && line[^1] == '\r')
                {
                    line.Length--;
                }

                return line.ToString();
            }

            AppendAscii(line, _readBuffer.AsSpan(_readStart, _readEnd - _readStart), maxLength);
            _readStart = _readEnd = 0;

            int received;
            try
            {
                received = await socket.ReceiveAsync(_readBuffer.AsMemory(), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                throw new SysBotConnectionException($"Receive from {Description} failed: {ex.SocketErrorCode}.", ex);
            }
            catch (ObjectDisposedException ex)
            {
                throw new SysBotConnectionException($"Connection to {Description} was closed.", ex);
            }

            if (received == 0)
            {
                throw new SysBotConnectionException($"Connection to {Description} was closed by the Switch.");
            }

            _readEnd = received;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket is not null)
        {
            try
            {
                if (socket.Connected)
                {
                    socket.Shutdown(SocketShutdown.Both);
                }
            }
            catch (SocketException)
            {
                // Peer already gone; nothing to do.
            }
            finally
            {
                socket.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    private Socket RequireSocket()
    {
        if (_disposed)
        {
            throw new SysBotConnectionException($"Connection to {Description} was closed.");
        }

        return _socket ?? throw new SysBotConnectionException($"Not connected to {Description}.");
    }

    private static void AppendAscii(StringBuilder line, ReadOnlySpan<byte> bytes, int maxLength)
    {
        if (line.Length + bytes.Length > maxLength)
        {
            throw new SysBotProtocolException($"Response line exceeded {maxLength} bytes.");
        }

        foreach (var b in bytes)
        {
            line.Append((char)b);
        }
    }

    private static void ConfigureKeepAlive(Socket socket)
    {
        // OS keep-alive detects a Switch that vanished (sleep, Wi-Fi drop) on an idle connection.
        // The session heartbeat is the primary detector; this is a backstop.
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
        {
            // Not all platforms expose per-socket keep-alive tuning; defaults still apply.
        }
    }
}

public sealed class TcpSysBotTransportFactory : ISysBotTransportFactory
{
    public ISysBotTransport Create(SwitchEndpoint endpoint) => new TcpSysBotTransport(endpoint);
}
