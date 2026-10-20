using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NxHub.Protocol.Transport;

namespace NxHub.Protocol.SysBotBase;

public sealed record SysBotClientOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for a response line. sys-botbase 2.x sleeps 50 ms after every command.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Largest accepted response line. <c>pixelPeek</c> returns up to ~1 MB of hex.</summary>
    public int MaxResponseLength { get; init; } = 2 * 1024 * 1024;

    /// <summary>Send <c>configure echoCommands 0</c> after connecting so responses stay one-per-command.</summary>
    public bool DisableEchoOnConnect { get; init; } = true;
}

/// <summary>The result of a request/response round trip.</summary>
public readonly record struct SysBotReply(string Line, TimeSpan RoundTrip);

/// <summary>
/// A connection to one sys-botbase module. All wire access is serialised. After any timeout
/// or I/O failure the client is <see cref="IsFaulted">faulted</see> and must be discarded:
/// sys-botbase responses carry no correlation ID, so a late reply would otherwise be mistaken
/// for the answer to the next command.
/// </summary>
public sealed class SysBotBaseClient : IAsyncDisposable
{
    private readonly ISysBotTransport _transport;
    private readonly SysBotClientOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _wire = new(1, 1);
    private volatile Exception? _fault;
    private volatile bool _disposed;

    public SysBotBaseClient(ISysBotTransport transport, SysBotClientOptions? options = null, ILogger? logger = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new SysBotClientOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    public SysBotCapabilities? Capabilities { get; private set; }

    /// <summary>Round trip of the handshake's <c>getVersion</c>, a first latency sample.</summary>
    public TimeSpan? HandshakeRoundTrip { get; private set; }

    public bool IsConnected => Capabilities is not null && _fault is null && !_disposed && _transport.IsConnected;

    public bool IsFaulted => _fault is not null;

    public Exception? Fault => _fault;

    public string Description => _transport.Description;

    /// <summary>Connects and performs the handshake (echo off, <c>getVersion</c>).</summary>
    public async Task<SysBotCapabilities> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_options.ConnectTimeout);
            try
            {
                await _transport.ConnectAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SysBotTimeoutException($"Timed out connecting to {Description} after {_options.ConnectTimeout.TotalSeconds:0.#} s.");
            }
        }

        if (_options.DisableEchoOnConnect)
        {
            await SendAsync(SysBotCommands.DisableEcho(), cancellationToken).ConfigureAwait(false);
        }

        var reply = await QueryAsync(SysBotCommands.GetVersion(), cancellationToken).ConfigureAwait(false);
        var capabilities = SysBotCapabilities.FromVersion(reply.Line);
        Capabilities = capabilities;
        HandshakeRoundTrip = reply.RoundTrip;
        _logger.LogInformation(
            "sys-botbase handshake with {Endpoint}: version {Version} ({Implementation}) in {RoundTripMs:0.0} ms",
            Description,
            capabilities.RawVersion,
            capabilities.Implementation,
            reply.RoundTrip.TotalMilliseconds);
        return capabilities;
    }

    /// <summary>Sends a command that produces no response.</summary>
    public async Task SendAsync(SysBotCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Response != ResponseKind.None)
        {
            throw new ArgumentException($"'{command.Name}' returns a response; use {nameof(QueryAsync)}.", nameof(command));
        }

        await AcquireWireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnusable();
            await WriteAsync(command, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("→ {Command}", command.Text);
        }
        finally
        {
            _wire.Release();
        }
    }

    /// <summary>Sends a command and reads its single response line.</summary>
    public async Task<SysBotReply> QueryAsync(SysBotCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Response != ResponseKind.Line)
        {
            throw new ArgumentException($"'{command.Name}' does not return a response; use {nameof(SendAsync)}.", nameof(command));
        }

        await AcquireWireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnusable();
            var stopwatch = Stopwatch.StartNew();
            await WriteAsync(command, cancellationToken).ConfigureAwait(false);

            var responseTimeout = command.ResponseTimeout ?? _options.ResponseTimeout;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(responseTimeout);
            string line;
            try
            {
                line = await _transport.ReadLineAsync(_options.MaxResponseLength, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                // Either way the response may still arrive later, so the stream is now unsafe.
                var fault = cancellationToken.IsCancellationRequested
                    ? new SysBotConnectionException($"'{command.Name}' was cancelled while awaiting its response; connection discarded.", ex)
                    : new SysBotTimeoutException($"No response to '{command.Name}' within {responseTimeout.TotalMilliseconds:0} ms.", ex);
                SetFault(fault);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw fault;
            }
            catch (Exception ex) when (ex is SysBotException)
            {
                SetFault(ex);
                throw;
            }

            stopwatch.Stop();
            _logger.LogDebug("→ {Command} ← {Response} ({RoundTripMs:0.0} ms)", command.Text, SysBotResponses.Printable(line), stopwatch.Elapsed.TotalMilliseconds);
            return new SysBotReply(line, stopwatch.Elapsed);
        }
        finally
        {
            _wire.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // _wire is deliberately not disposed: an in-flight operation still releases it in its
        // finally block, and queued callers then fail cleanly in ThrowIfUnusable.
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task WriteAsync(SysBotCommand command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.SendTimeout);
        try
        {
            await _transport.SendAsync(command.Encode(), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // A partially written line would corrupt the next command.
            SetFault(cancellationToken.IsCancellationRequested
                ? new SysBotConnectionException($"'{command.Name}' was cancelled while sending; connection discarded.", ex)
                : new SysBotTimeoutException($"Sending '{command.Name}' timed out.", ex));
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw _fault!;
        }
        catch (SysBotException ex)
        {
            SetFault(ex);
            throw;
        }
    }

    private async Task AcquireWireAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _wire.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            // The session discarded this client while the caller was queued behind another command.
            throw new SysBotConnectionException($"Connection to {Description} was closed.", ex);
        }
    }

    private void ThrowIfUnusable()
    {
        if (_disposed)
        {
            throw new SysBotConnectionException($"Connection to {Description} was closed.");
        }

        if (_fault is { } fault)
        {
            throw new SysBotConnectionException($"Connection to {Description} is no longer usable: {fault.Message}", fault);
        }
    }

    private void SetFault(Exception fault)
    {
        if (_fault is null)
        {
            _fault = fault;
            _logger.LogWarning("Connection to {Endpoint} faulted: {Reason}", Description, fault.Message);
        }
    }
}
