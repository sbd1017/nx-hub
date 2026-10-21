using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NxHub.Simulator;

/// <summary>Which upstream behaviour to imitate.</summary>
public enum SimulatorFlavour
{
    /// <summary>olliz0r/sys-botbase 2.x: accepts LF or CRLF, many clients, reports "2.5".</summary>
    SysBotBase,

    /// <summary>sys-botbase-cpp 3.x: requires CRLF, one client at a time, reports "3.33", supports ping.</summary>
    SysBotBaseCpp,
}

public sealed record SimulatorOptions
{
    public SimulatorFlavour Flavour { get; init; } = SimulatorFlavour.SysBotBase;

    public IPAddress Address { get; init; } = IPAddress.Loopback;

    /// <summary>0 picks a free port.</summary>
    public int Port { get; init; }

    public string? VersionOverride { get; init; }

    /// <summary>Title ID reported by getTitleID; null means "no game running" (empty line).</summary>
    public ulong? TitleId { get; init; } = 0x0100ABCD12340000;

    /// <summary>Artificial delay before every response.</summary>
    public TimeSpan ResponseDelay { get; init; } = TimeSpan.Zero;

    /// <summary>When false, pixelPeek returns an empty line (as when the debug-unit flag is not patched).</summary>
    public bool ScreenshotsWork { get; init; } = true;

    /// <summary>
    /// Serve an interactive pretend HOME menu instead of a test pattern: D-pad left/right moves the
    /// selection, A opens a tile, B or HOME goes back. Used by NX Hub's public demo mode.
    /// </summary>
    public bool InteractiveScreen { get; init; }
}

/// <summary>
/// A TCP server that speaks enough of the sys-botbase protocol to exercise NX Hub without a Switch.
/// Behaviour is modelled on upstream source as documented in docs/PROTOCOL.md. It is a test
/// double: passing against it says nothing about real hardware.
/// </summary>
public sealed class SysBotSimulator : IAsyncDisposable
{
    private static readonly HashSet<string> ButtonTokens =
    [
        "A", "B", "X", "Y", "L", "R", "ZL", "ZR", "PLUS", "MINUS", "DUP", "DU", "DDOWN", "DD", "DLEFT", "DL",
        "DRIGHT", "DR", "HOME", "CAPTURE", "LSTICK", "RSTICK", "PALMA", "UNUSED",
    ];

    private readonly SimulatorOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Guid, TcpClient> _clients = new();
    private readonly ConcurrentQueue<string> _received = new();
    private readonly object _stateGate = new();
    private readonly HashSet<string> _held = [];
    private readonly Dictionary<string, (int X, int Y)> _sticks = new() { ["LEFT"] = (0, 0), ["RIGHT"] = (0, 0) };
    private Task? _acceptLoop;
    private volatile bool _muted;
    private volatile bool _refuseNewClients;
    private int _controllerAttached;
    private int _dropAfter = -1;
    private int _connectionCount;

    public SysBotSimulator(SimulatorOptions? options = null)
    {
        _options = options ?? new SimulatorOptions();
        _listener = new TcpListener(_options.Address, _options.Port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Version => _options.VersionOverride ?? (_options.Flavour == SimulatorFlavour.SysBotBaseCpp ? "3.33" : "2.5");

    /// <summary>Every command line received, in order (without terminators).</summary>
    public IReadOnlyList<string> ReceivedCommands => _received.ToArray();

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    public bool ControllerAttached => Volatile.Read(ref _controllerAttached) == 1;

    public IReadOnlyCollection<string> HeldButtons
    {
        get
        {
            lock (_stateGate)
            {
                return _held.ToArray();
            }
        }
    }

    public (int X, int Y) GetStick(string side)
    {
        lock (_stateGate)
        {
            return _sticks[side];
        }
    }

    /// <summary>Stop answering (simulates a hung module); the socket stays open.</summary>
    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    /// <summary>Refuse new connections (simulates the module being unavailable).</summary>
    public bool RefuseNewClients
    {
        get => _refuseNewClients;
        set => _refuseNewClients = value;
    }

    /// <summary>Close each connection after it has received this many more commands.</summary>
    public void DropConnectionsAfter(int commands) => Volatile.Write(ref _dropAfter, commands);

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    /// <summary>Abruptly closes every client connection (simulates Wi-Fi loss or sleep).</summary>
    /// <summary>Clients currently connected.</summary>
    public int ConnectedClients => _clients.Count;

    public void DisconnectAllClients()
    {
        foreach (var (id, client) in _clients)
        {
            _clients.TryRemove(id, out _);
            try
            {
                // The serving task may be closing this client at the same moment.
                if (client.Client is { } socket)
                {
                    socket.LingerState = new LingerOption(true, 0);
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or NullReferenceException)
            {
            }

            client.Dispose();
        }
    }

    public void ClearReceived() => _received.Clear();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        DisconnectAllClients();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (_refuseNewClients)
            {
                client.Client.LingerState = new LingerOption(true, 0);
                client.Dispose();
                continue;
            }

            if (_options.Flavour == SimulatorFlavour.SysBotBaseCpp && !_clients.IsEmpty)
            {
                // sys-botbase-cpp serves one client; additional connections sit unanswered.
                _ = HoldUnservedAsync(client, stop);
                continue;
            }

            Interlocked.Increment(ref _connectionCount);
            var id = Guid.NewGuid();
            _clients[id] = client;
            _ = Task.Run(() => ServeAsync(id, client, stop), stop);
        }
    }

    private static async Task HoldUnservedAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task ServeAsync(Guid id, TcpClient client, CancellationToken stop)
    {
        var budget = Volatile.Read(ref _dropAfter);
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            var pending = new StringBuilder();
            while (!stop.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, stop).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                pending.Append(Encoding.ASCII.GetString(buffer, 0, read));
                while (TryTakeLine(pending, out var line))
                {
                    _received.Enqueue(line);
                    if (budget > 0 && --budget == 0)
                    {
                        client.Client.LingerState = new LingerOption(true, 0);
                        return;
                    }

                    var response = Handle(line);
                    if (response is null || _muted)
                    {
                        continue;
                    }

                    if (_options.ResponseDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(_options.ResponseDelay, stop).ConfigureAwait(false);
                    }

                    var bytes = Encoding.ASCII.GetBytes(response + "\n");
                    await stream.WriteAsync(bytes, stop).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Client went away.
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Dispose();
        }
    }

    private bool TryTakeLine(StringBuilder pending, out string line)
    {
        var text = pending.ToString();
        var terminator = _options.Flavour == SimulatorFlavour.SysBotBaseCpp ? "\r\n" : "\n";
        var index = text.IndexOf(terminator, StringComparison.Ordinal);
        if (index < 0)
        {
            line = string.Empty;
            return false;
        }

        line = text[..index].TrimEnd('\r');
        pending.Remove(0, index + terminator.Length);
        return true;
    }

    /// <summary>Returns the response line, or null when the command produces no output.</summary>
    private string? Handle(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var args = parts[1..];
        switch (parts[0])
        {
            case "getVersion":
                return Version;
            case "getTitleID":
                return _options.TitleId is { } tid ? tid.ToString("X16", CultureInfo.InvariantCulture) : string.Empty;
            case "getTitleVersion":
                return _options.TitleId is null ? string.Empty : "0000000000000001";
            case "getBuildID":
                return _options.TitleId is null ? string.Empty : "0123456789abcdef";
            case "getSystemLanguage":
                return "2";
            case "pixelPeek":
                return !_options.ScreenshotsWork ? string.Empty : _options.InteractiveScreen ? DemoFrameHex() : ScreenHex.Value;
            case "ping" when _options.Flavour == SimulatorFlavour.SysBotBaseCpp && args.Length == 1:
                return args[0];
            case "configure":
                return null;
            case "press" when args.Length == 1:
                AttachController();
                lock (_stateGate)
                {
                    var button = NormaliseButton(args[0]);
                    _held.Add(button);
                    MoveDemoCursor(button);
                }

                return null;
            case "release" when args.Length == 1:
                AttachController();
                lock (_stateGate)
                {
                    _held.Remove(NormaliseButton(args[0]));
                }

                return null;
            case "click" when args.Length == 1:
                AttachController();
                return null;
            case "setStick" when args.Length == 3 && (args[0] == "LEFT" || args[0] == "RIGHT"):
                AttachController();
                lock (_stateGate)
                {
                    _sticks[args[0]] = (int.Parse(args[1], CultureInfo.InvariantCulture), int.Parse(args[2], CultureInfo.InvariantCulture));
                }

                return null;
            case "detachController":
                // Like upstream, detaching does not clear the stored button state.
                Volatile.Write(ref _controllerAttached, 0);
                return null;
            default:
                // Upstream: unknown commands produce no output at all.
                return null;
        }
    }

    private const int DemoTiles = 6;

    private static readonly Lazy<string> ScreenHex = new(() => LoadHex("NxHub.Simulator.screen.jpg"));

    private static readonly ConcurrentDictionary<string, string> DemoFrames = new();

    private int _demoCursor;
    private bool _demoOpen;

    /// <summary>Which pretend-menu tile is selected, and whether it is "open" (interactive screen only).</summary>
    public (int Cursor, bool Open) DemoState
    {
        get
        {
            lock (_stateGate)
            {
                return (_demoCursor, _demoOpen);
            }
        }
    }

    private void MoveDemoCursor(string button)
    {
        switch (button)
        {
            case "DLEFT" when !_demoOpen:
                _demoCursor = Math.Max(0, _demoCursor - 1);
                break;
            case "DRIGHT" when !_demoOpen:
                _demoCursor = Math.Min(DemoTiles - 1, _demoCursor + 1);
                break;
            case "A":
                _demoOpen = true;
                break;
            case "B" or "HOME":
                _demoOpen = false;
                break;
        }
    }

    private string DemoFrameHex()
    {
        string name;
        lock (_stateGate)
        {
            name = $"NxHub.Simulator.demo.{(_demoOpen ? "open" : "home")}-{_demoCursor}.jpg";
        }

        return DemoFrames.GetOrAdd(name, LoadHex);
    }

    private static string LoadHex(string resource)
    {
        using var stream = typeof(SysBotSimulator).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Simulator asset '{resource}' is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Convert.ToHexString(memory.ToArray());
    }

    private void AttachController() => Volatile.Write(ref _controllerAttached, 1);

    private string NormaliseButton(string token)
    {
        if (!ButtonTokens.Contains(token))
        {
            // sys-botbase 2.x falls back to A for unknown tokens.
            return _options.Flavour == SimulatorFlavour.SysBotBase ? "A" : "?";
        }

        return token switch
        {
            "DU" => "DUP",
            "DD" => "DDOWN",
            "DL" => "DLEFT",
            "DR" => "DRIGHT",
            _ => token,
        };
    }
}
