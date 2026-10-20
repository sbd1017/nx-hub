using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NxHub.Core.Devices;
using NxHub.Core.Diagnostics;
using NxHub.Core.Events;
using NxHub.Core.Sessions;
using NxHub.Protocol.Transport;

namespace NxHub.Core;

/// <summary>
/// Owns the set of configured Switches and one <see cref="SwitchSession"/> per Switch.
/// Configuration changes go through here so storage and live sessions stay consistent.
/// </summary>
public sealed class SwitchManager : IAsyncDisposable
{
    private readonly ISwitchDeviceRepository _repository;
    private readonly ISysBotTransportFactory _transports;
    private readonly SessionOptions _options;
    private readonly IEventPublisher _events;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _time;
    private readonly DiagnosticsRunner _diagnostics;
    private readonly ConcurrentDictionary<string, SwitchSession> _sessions = new();
    private readonly SemaphoreSlim _configLock = new(1, 1);

    public SwitchManager(
        ISwitchDeviceRepository repository,
        ISysBotTransportFactory transports,
        SessionOptions options,
        IEventPublisher events,
        DiagnosticsRunner diagnostics,
        ILoggerFactory loggerFactory,
        TimeProvider time)
    {
        _repository = repository;
        _transports = transports;
        _options = options;
        _events = events;
        _diagnostics = diagnostics;
        _loggerFactory = loggerFactory;
        _time = time;
    }

    public DiagnosticsRunner Diagnostics => _diagnostics;

    /// <summary>Loads stored Switches and starts their sessions.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var device in await _repository.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            StartSession(device);
        }
    }

    public IReadOnlyList<SwitchSession> Sessions => _sessions.Values.OrderBy(s => s.Device.CreatedAt).ToArray();

    public SwitchSession GetSession(string id) =>
        _sessions.TryGetValue(id, out var session) ? session : throw Errors.Errors.SwitchNotFound(id);

    public async Task<SwitchSession> AddAsync(SwitchDeviceInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (name, host, port, enabled) = input.Validate();
        var now = _time.GetUtcNow();
        var device = new SwitchDevice
        {
            Id = SwitchDevice.NewId(),
            Name = name,
            Host = host,
            Port = port,
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _configLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _repository.AddAsync(device, cancellationToken).ConfigureAwait(false);
            var session = StartSession(device);
            session.LogEvent(Activity.ActivityLevel.Info, Activity.ActivityCategory.Configuration, $"Added {name} ({host}:{port}).");
            _events.Publish(new NxHubEvent(EventTypes.SwitchAdded, device.Id, now, device));
            return session;
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task<SwitchSession> UpdateAsync(string id, SwitchDeviceInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var session = GetSession(id);
        var (name, host, port, enabled) = input.Validate();

        await _configLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = session.Device with
            {
                Name = name,
                Host = host,
                Port = port,
                Enabled = enabled,
                UpdatedAt = _time.GetUtcNow(),
            };
            if (!await _repository.UpdateAsync(updated, cancellationToken).ConfigureAwait(false))
            {
                throw Errors.Errors.SwitchNotFound(id);
            }

            await session.ApplyDeviceAsync(updated, cancellationToken).ConfigureAwait(false);
            _events.Publish(new NxHubEvent(EventTypes.SwitchUpdated, id, updated.UpdatedAt, updated));
            return session;
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await _configLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryRemove(id, out var session))
            {
                throw Errors.Errors.SwitchNotFound(id);
            }

            await session.DisposeAsync().ConfigureAwait(false);
            await _repository.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            _diagnostics.Forget(id);
            _events.Publish(new NxHubEvent(EventTypes.SwitchRemoved, id, _time.GetUtcNow(), null));
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var sessions = _sessions.Values.ToArray();
        _sessions.Clear();
        await Task.WhenAll(sessions.Select(s => s.DisposeAsync().AsTask())).ConfigureAwait(false);
        _configLock.Dispose();
    }

    private SwitchSession StartSession(SwitchDevice device)
    {
        var session = new SwitchSession(device, _transports, _options, _events, _loggerFactory, _time);
        if (!_sessions.TryAdd(device.Id, session))
        {
            throw new InvalidOperationException($"A session for '{device.Id}' already exists.");
        }

        session.Start();
        return session;
    }
}
