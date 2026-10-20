using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using NxHub.Protocol.SysBotBase;
using NxHub.Protocol.Transport;

namespace NxHub.Tests.Shared;

/// <summary>
/// In-memory stand-in for a Switch running sys-botbase, used through <see cref="ISysBotTransportFactory"/>.
/// Unlike the TCP simulator it needs no sockets, so tests can control failures precisely.
/// </summary>
internal sealed class FakeSwitch : ISysBotTransportFactory
{
    private readonly ConcurrentQueue<string> _sent = new();
    private readonly ConcurrentDictionary<FakeTransport, byte> _open = new();
    private int _connectAttempts;

    public string Version { get; set; } = "2.5";

    /// <summary>When false, connection attempts are refused.</summary>
    public bool Online { get; set; } = true;

    /// <summary>When false, request/response commands never get an answer.</summary>
    public bool Responsive { get; set; } = true;

    public string TitleIdResponse { get; set; } = "0100ABCD12340000";

    /// <summary>pixelPeek response: a tiny JPEG-looking payload, or empty when screenshots fail.</summary>
    public string ScreenResponse { get; set; } = "FFD8FFE000104A464946";

    public int ScreenRequests => _sent.Count(s => s == "pixelPeek");

    public List<SwitchEndpoint> Endpoints { get; } = [];

    public IReadOnlyList<string> Sent => [.. _sent];

    public int ConnectAttempts => Volatile.Read(ref _connectAttempts);

    public int OpenConnections => _open.Count;

    public void ClearSent() => _sent.Clear();

    /// <summary>Count of sent lines equal to <paramref name="line"/>.</summary>
    public int CountSent(string line) => _sent.Count(s => s == line);

    /// <summary>Closes every open connection from the "Switch" side.</summary>
    public void DropConnections()
    {
        foreach (var transport in _open.Keys)
        {
            transport.CloseFromRemote();
        }
    }

    public ISysBotTransport Create(SwitchEndpoint endpoint)
    {
        lock (Endpoints)
        {
            Endpoints.Add(endpoint);
        }

        return new FakeTransport(this, endpoint);
    }

    private string? Respond(string line)
    {
        var name = line.Split(' ')[0];
        if (!Responsive)
        {
            return null;
        }

        return name switch
        {
            "getVersion" => Version,
            "getTitleID" => TitleIdResponse,
            "pixelPeek" => ScreenResponse,
            _ => null,
        };
    }

    internal sealed class FakeTransport : ISysBotTransport
    {
        private readonly FakeSwitch _owner;
        private readonly SwitchEndpoint _endpoint;
        private readonly Channel<string> _responses = Channel.CreateUnbounded<string>();
        private bool _connected;

        public FakeTransport(FakeSwitch owner, SwitchEndpoint endpoint)
        {
            _owner = owner;
            _endpoint = endpoint;
        }

        public bool IsConnected => _connected;

        public string Description => $"fake://{_endpoint}";

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _owner._connectAttempts);
            if (!_owner.Online)
            {
                throw new SysBotConnectionException($"Could not connect to {Description}: ConnectionRefused.");
            }

            _connected = true;
            _owner._open[this] = 0;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (!_connected)
            {
                throw new SysBotConnectionException("Connection closed.");
            }

            var text = Encoding.ASCII.GetString(data.Span);
            if (!text.EndsWith("\r\n", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Commands must be CRLF terminated.");
            }

            var line = text[..^2];
            _owner._sent.Enqueue(line);
            if (_owner.Respond(line) is { } response)
            {
                _responses.Writer.TryWrite(response);
            }

            return Task.CompletedTask;
        }

        public async Task<string> ReadLineAsync(int maxLength, CancellationToken cancellationToken)
        {
            try
            {
                return await _responses.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                throw new SysBotConnectionException($"Connection to {Description} was closed by the Switch.");
            }
        }

        public void CloseFromRemote()
        {
            _connected = false;
            _responses.Writer.TryComplete();
            _owner._open.TryRemove(this, out _);
        }

        public ValueTask DisposeAsync()
        {
            _connected = false;
            _responses.Writer.TryComplete();
            _owner._open.TryRemove(this, out _);
            return ValueTask.CompletedTask;
        }
    }
}
