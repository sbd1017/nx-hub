using Microsoft.Extensions.Logging;
using NxHub.Core.Activity;
using NxHub.Core.Devices;
using NxHub.Core.Errors;
using NxHub.Core.Events;
using NxHub.Protocol.SysBotBase;
using NxHub.Protocol.Transport;

namespace NxHub.Core.Sessions;

/// <summary>
/// Owns the connection to one Switch: connects, performs the handshake, heartbeats, detects loss,
/// reconnects with exponential backoff, and shuts down cleanly. One instance per configured Switch.
/// </summary>
/// <remarks>
/// A single background supervisor loop owns the connection lifecycle. Callers never touch the
/// socket directly; they run operations through <see cref="ExecuteAsync{T}"/>, which serialises
/// wire access (inside <see cref="SysBotBaseClient"/>) and reports failures back to the loop.
/// </remarks>
public sealed class SwitchSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ISysBotTransportFactory _transports;
    private readonly SessionOptions _options;
    private readonly IEventPublisher _events;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ILogger<SysBotBaseClient> _clientLogger;
    private readonly TimeProvider _time;
    private readonly AsyncSignal _wake = new();
    private readonly CancellationTokenSource _stop = new();

    private Task? _loop;
    private SwitchDevice _device;
    private bool _autoConnect;
    private ConnectionState _state = ConnectionState.Disconnected;
    private DateTimeOffset _stateSince;
    private DateTimeOffset? _connectedSince;
    private int _attempt;
    private DateTimeOffset? _nextAttemptAt;
    private DateTimeOffset? _lastAttemptAt;
    private int _connectionCount;
    private int _lossCount;
    private ConnectionError? _lastError;
    private LatencyStats? _latency;
    private SysBotCapabilities? _capabilities;
    private ActiveConnection? _active;
    private DateTimeOffset _lastQueryAt;
    private TaskCompletionSource<bool> _attemptCompleted = NewAttemptSource();
    private TaskCompletionSource _stateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public SwitchSession(
        SwitchDevice device,
        ISysBotTransportFactory transports,
        SessionOptions options,
        IEventPublisher events,
        ILoggerFactory loggerFactory,
        TimeProvider time)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _transports = transports;
        _options = options;
        _events = events;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SwitchSession>();
        // Created up front: the logger factory may already be disposed if the host stops before the loop runs.
        _clientLogger = loggerFactory.CreateLogger<SysBotBaseClient>();
        _time = time;
        _stateSince = time.GetUtcNow();
        _autoConnect = device.Enabled;
        Activity = new ActivityLog(device.Id, events, time);
        Controller = new SwitchController(this, options.Controller, time, loggerFactory.CreateLogger<SwitchController>());
        Screen = new Screen.ScreenCapture(this, time);
        Macros = new Macros.MacroRunner(this, events, time, loggerFactory.CreateLogger<Macros.MacroRunner>());
    }

    public string SwitchId => _device.Id;

    public SwitchDevice Device
    {
        get
        {
            lock (_gate)
            {
                return _device;
            }
        }
    }

    public ActivityLog Activity { get; }

    public SwitchController Controller { get; }

    public Screen.ScreenCapture Screen { get; }

    public Macros.MacroRunner Macros { get; }

    internal TimeProvider Time => _time;

    internal ILogger Logger => _logger;

    public ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Starts the supervisor loop. Connects immediately if the device is enabled.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _loop = Task.Run(RunAsync);
        }
    }

    public SwitchStatus GetStatus()
    {
        lock (_gate)
        {
            return BuildStatusLocked();
        }
    }

    /// <summary>
    /// Requests a connection and waits (up to <see cref="SessionOptions.ConnectWaitTimeout"/>)
    /// for the next attempt to finish. Returns the resulting status either way.
    /// </summary>
    public async Task<SwitchStatus> ConnectAsync(CancellationToken cancellationToken)
    {
        Task<bool> attempt;
        lock (_gate)
        {
            if (_state == ConnectionState.Connected)
            {
                _autoConnect = true;
                return BuildStatusLocked();
            }

            _autoConnect = true;
            attempt = _attemptCompleted.Task;
        }

        LogEvent(ActivityLevel.Info, ActivityCategory.Connection, "Connect requested.");
        _wake.Set();
        try
        {
            await attempt.WaitAsync(_options.ConnectWaitTimeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Still trying; the caller gets the current state.
        }

        return GetStatus();
    }

    /// <summary>Stops auto-connect, releases held inputs (best effort) and closes the connection.</summary>
    public async Task<SwitchStatus> DisconnectAsync(CancellationToken cancellationToken)
    {
        ActiveConnection? active;
        lock (_gate)
        {
            if (!_autoConnect && _active is null)
            {
                return BuildStatusLocked();
            }

            _autoConnect = false;
            active = _active;
            if (active is not null && _state == ConnectionState.Connected)
            {
                SetStateLocked(ConnectionState.Disconnecting);
            }
        }

        PublishState();
        LogEvent(ActivityLevel.Info, ActivityCategory.Connection, "Disconnect requested.");
        await Macros.StopAsync().ConfigureAwait(false);
        if (active is not null)
        {
            await Controller.ReleaseEverythingBeforeDisconnectAsync(active.Client).ConfigureAwait(false);
            active.Cancel(restart: false);
        }

        _wake.Set();
        await WaitForStateAsync(s => s == ConnectionState.Disconnected, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return GetStatus();
    }

    /// <summary>Applies an updated configuration. Endpoint changes force a reconnect.</summary>
    public async Task ApplyDeviceAsync(SwitchDevice updated, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updated);
        bool endpointChanged;
        ActiveConnection? active;
        lock (_gate)
        {
            endpointChanged = _device.Endpoint != updated.Endpoint;
            _device = updated;
            active = _active;
        }

        if (!updated.Enabled)
        {
            await DisconnectAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            _autoConnect = true;
        }

        if (endpointChanged && active is not null)
        {
            LogEvent(ActivityLevel.Info, ActivityCategory.Configuration, $"Address changed to {updated.Endpoint}; reconnecting.");
            await Controller.ReleaseEverythingBeforeDisconnectAsync(active.Client).ConfigureAwait(false);
            active.Cancel(restart: true);
        }

        _wake.Set();
    }

    /// <summary>
    /// Runs an operation against the live connection. Connection failures are reported to the
    /// supervisor (which reconnects) and surfaced as <see cref="NxHubException"/>.
    /// </summary>
    internal async Task<T> ExecuteAsync<T>(Func<SysBotBaseClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ActiveConnection? connection;
        string name;
        lock (_gate)
        {
            connection = _state == ConnectionState.Connected ? _active : null;
            name = _device.Name;
        }

        if (connection is null || !connection.Client.IsConnected)
        {
            throw Errors.Errors.NotConnected(name);
        }

        try
        {
            return await operation(connection.Client, cancellationToken).ConfigureAwait(false);
        }
        catch (SysBotTimeoutException ex)
        {
            connection.MarkLost(ex);
            throw Errors.Errors.Timeout(name, ex);
        }
        catch (SysBotConnectionException ex)
        {
            connection.MarkLost(ex);
            throw Errors.Errors.ConnectionLost(name, ex);
        }
        catch (SysBotProtocolException ex)
        {
            // The line itself was framed correctly, so the stream is still in sync.
            throw Errors.Errors.Protocol(name, ex);
        }
        catch (ObjectDisposedException)
        {
            throw Errors.Errors.NotConnected(name);
        }
    }

    internal Task ExecuteAsync(Func<SysBotBaseClient, CancellationToken, Task> operation, CancellationToken cancellationToken) =>
        ExecuteAsync<bool>(
            async (client, ct) =>
            {
                await operation(client, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>Runs a request/response command and records its round trip as a latency sample.</summary>
    internal Task<SysBotReply> QueryAsync(SysBotCommand command, CancellationToken cancellationToken) =>
        ExecuteAsync(
            async (client, ct) =>
            {
                var reply = await client.QueryAsync(command, ct).ConfigureAwait(false);
                RecordLatency(reply.RoundTrip);
                return reply;
            },
            cancellationToken);

    internal void PublishControllerState() =>
        _events.Publish(new NxHubEvent(EventTypes.ControllerState, SwitchId, _time.GetUtcNow(), Controller.GetSnapshot()));

    internal void LogEvent(ActivityLevel level, ActivityCategory category, string message)
    {
        Activity.Add(level, category, message);
        var logLevel = level switch
        {
            ActivityLevel.Error => LogLevel.Error,
            ActivityLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        _logger.Log(logLevel, "[{SwitchId}] {Category}: {Message}", SwitchId, category, message);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await Macros.StopAsync().ConfigureAwait(false);

        ActiveConnection? active;
        Task? loop;
        lock (_gate)
        {
            _autoConnect = false;
            active = _active;
            loop = _loop;
        }

        if (active is not null)
        {
            await Controller.ReleaseEverythingBeforeDisconnectAsync(active.Client).ConfigureAwait(false);
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _wake.Set();
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch (ObjectDisposedException)
            {
                // The host tore down shared services first; nothing left to clean up.
            }
        }

        Controller.Dispose();
        Macros.Dispose();
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        var stop = _stop.Token;
        var clientLogger = _clientLogger;

        while (!stop.IsCancellationRequested)
        {
            SwitchEndpoint endpoint;
            lock (_gate)
            {
                if (!_autoConnect)
                {
                    _attempt = 0;
                    _nextAttemptAt = null;
                    SetStateLocked(ConnectionState.Disconnected);
                }

                endpoint = _device.Endpoint;
            }

            if (State == ConnectionState.Disconnected && !AutoConnectRequested())
            {
                PublishState();
                try
                {
                    await _wake.WaitAsync(Timeout.InfiniteTimeSpan, _time, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            var connection = new ActiveConnection(new SysBotBaseClient(_transports.Create(endpoint), _options.Client, clientLogger), stop);
            DateTimeOffset? connectedAt = null;
            DateTimeOffset? failedAt = null;
            lock (_gate)
            {
                _active = connection;
                _nextAttemptAt = null;
                _lastAttemptAt = _time.GetUtcNow();
                SetStateLocked(_attempt == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting);
            }

            PublishState();

            try
            {
                var capabilities = await connection.Client.ConnectAsync(connection.Token).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                connectedAt = now;
                lock (_gate)
                {
                    _capabilities = capabilities;
                    _connectedSince = now;
                    _connectionCount++;
                    _lastQueryAt = now;
                    _lastError = null;
                    SetStateLocked(ConnectionState.Connected);
                }

                if (connection.Client.HandshakeRoundTrip is { } handshake)
                {
                    RecordLatency(handshake);
                }

                PublishState();
                LogEvent(
                    ActivityLevel.Info,
                    ActivityCategory.Connection,
                    $"Connected to {endpoint} (sys-botbase {capabilities.RawVersion}, {capabilities.Implementation}).");
                foreach (var warning in capabilities.Warnings)
                {
                    LogEvent(ActivityLevel.Warning, ActivityCategory.Protocol, warning);
                }

                CompleteAttempt(true);
                await Controller.OnConnectedAsync(connection.Client, connection.Token).ConfigureAwait(false);
                await HeartbeatAsync(connection).ConfigureAwait(false);
            }
            catch (Exception) when (connection.LostReason is null && connection.Token.IsCancellationRequested)
            {
                // Disconnect, reconfiguration or shutdown was requested.
            }
            catch (Exception ex)
            {
                var reason = connection.LostReason ?? ex;
                failedAt = _time.GetUtcNow();
                if (connectedAt is not null)
                {
                    lock (_gate)
                    {
                        _lossCount++;
                    }

                    LogEvent(ActivityLevel.Warning, ActivityCategory.Connection, $"Connection lost: {reason.Message}");
                }
                else
                {
                    LogEvent(ActivityLevel.Error, ActivityCategory.Connection, $"Connection attempt failed: {reason.Message}");
                }

                RecordError(reason, failedAt.Value);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_active, connection))
                    {
                        _active = null;
                    }

                    _connectedSince = null;
                }

                if (connectedAt is not null)
                {
                    Controller.OnConnectionLost();
                }

                await connection.DisposeAsync().ConfigureAwait(false);
            }

            // A failed attempt is reported to ConnectAsync waiters only after the state reflects it.
            var attemptFailed = failedAt is not null && connectedAt is null;
            if (stop.IsCancellationRequested)
            {
                if (attemptFailed)
                {
                    CompleteAttempt(false);
                }

                break;
            }

            var delay = TimeSpan.Zero;
            var scheduleRetry = false;
            lock (_gate)
            {
                if (connection.RestartRequested)
                {
                    _attempt = 0;
                }
                else if (_autoConnect)
                {
                    scheduleRetry = true;
                }
            }

            if (!scheduleRetry)
            {
                if (attemptFailed)
                {
                    CompleteAttempt(false);
                }

                continue;
            }

            lock (_gate)
            {
                // Measure from the failure itself so the schedule is exact and observable.
                var failed = failedAt ?? _time.GetUtcNow();
                if (connectedAt is { } at && failed - at >= _options.StableConnectionThreshold)
                {
                    _attempt = 0;
                }

                _attempt++;
                delay = _options.GetBackoffDelay(_attempt);
                _nextAttemptAt = failed + delay;
                delay = _nextAttemptAt.Value - _time.GetUtcNow();
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                SetStateLocked(ConnectionState.Reconnecting);
            }

            PublishState();
            if (attemptFailed)
            {
                CompleteAttempt(false);
            }

            _logger.LogInformation("[{SwitchId}] Reconnect attempt {Attempt} in {DelayMs:0} ms", SwitchId, _attempt, delay.TotalMilliseconds);
            try
            {
                await _wake.WaitAsync(delay, _time, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        lock (_gate)
        {
            _nextAttemptAt = null;
            SetStateLocked(ConnectionState.Disconnected);
        }

        PublishState();
    }

    private async Task HeartbeatAsync(ActiveConnection connection)
    {
        var token = connection.Token;
        while (true)
        {
            await Task.Delay(_options.HeartbeatInterval, _time, token).ConfigureAwait(false);
            if (connection.Client.Fault is { } fault)
            {
                throw fault;
            }

            DateTimeOffset lastQuery;
            lock (_gate)
            {
                lastQuery = _lastQueryAt;
            }

            if (_time.GetUtcNow() - lastQuery < _options.HeartbeatInterval)
            {
                continue;
            }

            var reply = await connection.Client.QueryAsync(SysBotCommands.GetVersion(), token).ConfigureAwait(false);
            RecordLatency(reply.RoundTrip);
        }
    }

    private bool AutoConnectRequested()
    {
        lock (_gate)
        {
            return _autoConnect;
        }
    }

    private void RecordLatency(TimeSpan roundTrip)
    {
        var ms = roundTrip.TotalMilliseconds;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _lastQueryAt = now;
            var average = _latency is null ? ms : (_latency.AverageMs * 0.8) + (ms * 0.2);
            _latency = new LatencyStats(Math.Round(ms, 1), Math.Round(average, 1), now);
        }
    }

    private void RecordError(Exception error, DateTimeOffset at)
    {
        var code = error switch
        {
            SysBotTimeoutException => "switch_timeout",
            SysBotConnectionException => "switch_connection_failed",
            SysBotProtocolException => "switch_protocol_error",
            _ => "internal_error",
        };
        if (code == "internal_error")
        {
            _logger.LogError(error, "[{SwitchId}] Unexpected session error", SwitchId);
        }

        lock (_gate)
        {
            _lastError = new ConnectionError(error.Message, code, at);
        }
    }

    private void CompleteAttempt(bool success)
    {
        TaskCompletionSource<bool> completed;
        lock (_gate)
        {
            completed = _attemptCompleted;
            _attemptCompleted = NewAttemptSource();
        }

        completed.TrySetResult(success);
    }

    private async Task WaitForStateAsync(Func<ConnectionState, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + timeout;
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (predicate(_state))
                {
                    return;
                }

                changed = _stateChanged.Task;
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            try
            {
                await changed.WaitAsync(remaining, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return;
            }
        }
    }

    private void SetStateLocked(ConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        _stateSince = _time.GetUtcNow();
        var changed = _stateChanged;
        _stateChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    private void PublishState()
    {
        var status = GetStatus();
        _events.Publish(new NxHubEvent(EventTypes.SwitchState, SwitchId, _time.GetUtcNow(), status));
    }

    private SwitchStatus BuildStatusLocked() => new()
    {
        SwitchId = _device.Id,
        State = _state,
        AutoConnect = _autoConnect,
        StateSince = _stateSince,
        ConnectedSince = _connectedSince,
        ReconnectAttempt = _state == ConnectionState.Connected ? 0 : _attempt,
        NextAttemptAt = _nextAttemptAt,
        LastAttemptAt = _lastAttemptAt,
        ConnectionCount = _connectionCount,
        ConnectionLossCount = _lossCount,
        LastError = _lastError,
        Latency = _latency,
        Capabilities = _state == ConnectionState.Connected ? _capabilities : null,
        Controller = Controller.GetSnapshot(),
    };

    private static TaskCompletionSource<bool> NewAttemptSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>One connection attempt and, if it succeeds, its lifetime.</summary>
    private sealed class ActiveConnection : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime;
        private Exception? _lostReason;

        public ActiveConnection(SysBotBaseClient client, CancellationToken stop)
        {
            Client = client;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        }

        public SysBotBaseClient Client { get; }

        public CancellationToken Token => _lifetime.Token;

        public Exception? LostReason => Volatile.Read(ref _lostReason);

        public bool RestartRequested { get; private set; }

        public void MarkLost(Exception reason)
        {
            Interlocked.CompareExchange(ref _lostReason, reason, null);
            TryCancel();
        }

        public void Cancel(bool restart)
        {
            RestartRequested = restart;
            TryCancel();
        }

        public async ValueTask DisposeAsync()
        {
            TryCancel();
            await Client.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
        }

        private void TryCancel()
        {
            try
            {
                _lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }
    }
}
