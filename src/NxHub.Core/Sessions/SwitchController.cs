using Microsoft.Extensions.Logging;
using NxHub.Core.Activity;
using NxHub.Core.Errors;
using NxHub.Protocol;
using NxHub.Protocol.SysBotBase;

namespace NxHub.Core.Sessions;

/// <summary>
/// Controller input for one Switch with safe press/hold/release semantics.
/// </summary>
/// <remarks>
/// <para>sys-botbase keeps the virtual controller state inside the Switch, so a button that is
/// pressed and never released stays held even after NX Hub disconnects. To prevent that:</para>
/// <list type="bullet">
/// <item>Timed presses always release in a <c>finally</c> block, even when cancelled.</item>
/// <item>Explicit holds have a watchdog that auto-releases after a maximum hold time.</item>
/// <item>Inputs still believed held when a connection drops are released on reconnect.</item>
/// <item>Disconnect and shutdown release everything first (best effort).</item>
/// </list>
/// </remarks>
public sealed class SwitchController : IDisposable
{
    private static readonly SwitchButton[] AllButtons = Enum.GetValues<SwitchButton>();
    private static readonly SwitchStick[] AllSticks = Enum.GetValues<SwitchStick>();
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(2);

    private readonly SwitchSession _session;
    private readonly ControllerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<SwitchButton, Tracked> _held = [];
    private readonly Dictionary<SwitchStick, TrackedStick> _sticks = [];
    private readonly Dictionary<SwitchButton, SemaphoreSlim> _buttonLocks;
    private readonly Dictionary<SwitchStick, SemaphoreSlim> _stickLocks;
    private bool _pendingNeutralise;
    private long _nextToken;

    internal SwitchController(SwitchSession session, ControllerOptions options, TimeProvider time, ILogger logger)
    {
        _session = session;
        _options = options;
        _time = time;
        _logger = logger;
        _buttonLocks = AllButtons.ToDictionary(b => b, _ => new SemaphoreSlim(1, 1));
        _stickLocks = AllSticks.ToDictionary(s => s, _ => new SemaphoreSlim(1, 1));
    }

    public ControllerOptions Options => _options;

    public ControllerSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new ControllerSnapshot(
                _held.Keys.Order().ToArray(),
                _sticks.ToDictionary(kv => kv.Key, kv => kv.Value.Position),
                _pendingNeutralise);
        }
    }

    /// <summary>Presses a button, waits, then releases it. The release is guaranteed to be attempted.</summary>
    public async Task PressAsync(SwitchButton button, TimeSpan? duration, CancellationToken cancellationToken, bool logActivity = true)
    {
        var hold = duration ?? _options.DefaultPressDuration;
        ValidateRange("durationMs", hold, _options.MinPressDuration, _options.MaxPressDuration);

        var buttonLock = _buttonLocks[button];
        await buttonLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendPressAsync(button, watchdog: null, cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(hold, _time, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await ReleaseTrackedAsync(button).ConfigureAwait(false);
            }

            if (logActivity)
            {
                _session.LogEvent(ActivityLevel.Info, ActivityCategory.Controller, $"Pressed {button} ({hold.TotalMilliseconds:0} ms).");
            }
        }
        finally
        {
            buttonLock.Release();
        }
    }

    /// <summary>Holds a button until <see cref="ReleaseAsync"/> or the watchdog fires.</summary>
    public async Task HoldAsync(SwitchButton button, TimeSpan? maxHold, CancellationToken cancellationToken, bool logActivity = true)
    {
        var limit = maxHold ?? _options.DefaultMaxHold;
        ValidateRange("maxHoldMs", limit, TimeSpan.FromMilliseconds(100), _options.MaxHold);

        bool refresh;
        lock (_gate)
        {
            refresh = _held.ContainsKey(button);
        }

        var buttonLock = _buttonLocks[button];
        await buttonLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendPressAsync(button, limit, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            buttonLock.Release();
        }

        // Re-holding an already held button just refreshes the watchdog (UIs do this while a finger stays down).
        if (!refresh && logActivity)
        {
            _session.LogEvent(ActivityLevel.Info, ActivityCategory.Controller, $"Holding {button} (auto-release after {limit.TotalSeconds:0.#} s).");
        }
    }

    public async Task ReleaseAsync(SwitchButton button, CancellationToken cancellationToken, bool logActivity = true)
    {
        DateTimeOffset? heldSince;
        lock (_gate)
        {
            heldSince = _held.TryGetValue(button, out var tracked) ? tracked.Since : null;
        }

        var buttonLock = _buttonLocks[button];
        await buttonLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReleaseTrackedAsync(button).ConfigureAwait(false);
        }
        finally
        {
            buttonLock.Release();
        }

        if (!logActivity)
        {
            return;
        }

        _session.LogEvent(
            ActivityLevel.Info,
            ActivityCategory.Controller,
            heldSince is { } since ? $"Released {button} (held {(_time.GetUtcNow() - since).TotalMilliseconds:0} ms)." : $"Released {button}.");
    }

    /// <summary>
    /// Moves a stick. With a duration it returns to centre afterwards; without one it stays put
    /// until moved again or the watchdog centres it.
    /// </summary>
    public async Task SetStickAsync(SwitchStick stick, StickPosition position, TimeSpan? duration, TimeSpan? maxHold, CancellationToken cancellationToken)
    {
        if (duration is { } d)
        {
            ValidateRange("durationMs", d, _options.MinPressDuration, _options.MaxPressDuration);
        }

        var limit = maxHold ?? _options.DefaultMaxHold;
        ValidateRange("maxHoldMs", limit, TimeSpan.FromMilliseconds(100), _options.MaxHold);

        var stickLock = _stickLocks[stick];
        await stickLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendStickAsync(stick, position, duration is null ? limit : null, cancellationToken).ConfigureAwait(false);
            if (duration is { } hold && !position.IsCentred)
            {
                try
                {
                    await Task.Delay(hold, _time, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await CentreTrackedAsync(stick).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            stickLock.Release();
        }
    }

    /// <summary>
    /// Releases every input NX Hub is tracking. With <paramref name="everything"/>, releases all
    /// buttons and centres both sticks regardless of tracking (useful after a crash or restart).
    /// </summary>
    public async Task ReleaseAllAsync(bool everything, CancellationToken cancellationToken)
    {
        SwitchButton[] buttons;
        SwitchStick[] sticks;
        lock (_gate)
        {
            buttons = everything ? AllButtons : [.. _held.Keys];
            sticks = everything ? AllSticks : [.. _sticks.Keys];
        }

        if (buttons.Length == 0 && sticks.Length == 0)
        {
            return;
        }

        await _session.ExecuteAsync(
            async (client, ct) =>
            {
                foreach (var button in buttons)
                {
                    await client.SendAsync(SysBotCommands.Release(button), ct).ConfigureAwait(false);
                    Untrack(button);
                }

                foreach (var stick in sticks)
                {
                    await client.SendAsync(SysBotCommands.SetStick(stick, StickPosition.Centre), ct).ConfigureAwait(false);
                    UntrackStick(stick);
                }
            },
            cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _pendingNeutralise = _held.Count > 0 || _sticks.Count > 0;
        }

        _session.LogEvent(
            ActivityLevel.Info,
            ActivityCategory.Controller,
            everything ? "Released all buttons and centred both sticks." : $"Released {buttons.Length} held button(s) and centred {sticks.Length} stick(s).");
    }

    /// <summary>
    /// Releases everything, then detaches the virtual controller. The next input re-attaches it.
    /// sys-botbase keeps the button state across a detach, hence the release first.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await ReleaseAllAsync(everything: true, cancellationToken).ConfigureAwait(false);
        await _session.ExecuteAsync((client, ct) => client.SendAsync(SysBotCommands.DetachController(), ct), cancellationToken).ConfigureAwait(false);
        _session.LogEvent(ActivityLevel.Info, ActivityCategory.Controller, "Virtual controller detached.");
    }

    /// <summary>Called by the session right after a successful handshake.</summary>
    internal async Task OnConnectedAsync(SysBotBaseClient client, CancellationToken cancellationToken)
    {
        SwitchButton[] buttons;
        SwitchStick[] sticks;
        lock (_gate)
        {
            (buttons, sticks) = _options.NeutraliseOnConnect switch
            {
                NeutraliseMode.Always => (AllButtons, AllSticks),
                NeutraliseMode.OnlyIfHeld when _pendingNeutralise => ([.. _held.Keys], [.. _sticks.Keys]),
                _ => ([], []),
            };

            if (_options.NeutraliseOnConnect == NeutraliseMode.Never)
            {
                ClearTrackingLocked();
            }
        }

        if (buttons.Length == 0 && sticks.Length == 0)
        {
            return;
        }

        foreach (var button in buttons)
        {
            await client.SendAsync(SysBotCommands.Release(button), cancellationToken).ConfigureAwait(false);
        }

        foreach (var stick in sticks)
        {
            await client.SendAsync(SysBotCommands.SetStick(stick, StickPosition.Centre), cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            ClearTrackingLocked();
        }

        _session.PublishControllerState();
        if (_options.NeutraliseOnConnect == NeutraliseMode.Always)
        {
            _session.LogEvent(ActivityLevel.Info, ActivityCategory.Controller, "Neutralised controller on connect.");
        }
        else
        {
            var names = string.Join(", ", buttons.Select(b => b.ToString()).Concat(sticks.Select(s => $"{s} stick")));
            _session.LogEvent(ActivityLevel.Warning, ActivityCategory.Controller, $"Released inputs left held when the connection dropped: {names}.");
        }
    }

    /// <summary>Called by the session when a connection ends without a clean release.</summary>
    internal void OnConnectionLost()
    {
        lock (_gate)
        {
            foreach (var tracked in _held.Values)
            {
                tracked.Watchdog?.Dispose();
            }

            foreach (var tracked in _sticks.Values)
            {
                tracked.Watchdog?.Dispose();
            }

            // Keep the entries: they describe what the Switch is probably still holding.
            foreach (var key in _held.Keys.ToArray())
            {
                _held[key] = _held[key] with { Watchdog = null };
            }

            foreach (var key in _sticks.Keys.ToArray())
            {
                _sticks[key] = _sticks[key] with { Watchdog = null };
            }

            _pendingNeutralise = _held.Count > 0 || _sticks.Count > 0;
        }

        _session.PublishControllerState();
    }

    /// <summary>Best-effort release of tracked inputs before an intentional disconnect.</summary>
    internal async Task ReleaseEverythingBeforeDisconnectAsync(SysBotBaseClient client)
    {
        SwitchButton[] buttons;
        SwitchStick[] sticks;
        lock (_gate)
        {
            buttons = [.. _held.Keys];
            sticks = [.. _sticks.Keys];
        }

        if ((buttons.Length == 0 && sticks.Length == 0) || !client.IsConnected)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(ReleaseTimeout, _time);
        try
        {
            foreach (var button in buttons)
            {
                await client.SendAsync(SysBotCommands.Release(button), timeout.Token).ConfigureAwait(false);
                Untrack(button);
            }

            foreach (var stick in sticks)
            {
                await client.SendAsync(SysBotCommands.SetStick(stick, StickPosition.Centre), timeout.Token).ConfigureAwait(false);
                UntrackStick(stick);
            }
        }
        catch (Exception ex) when (ex is SysBotException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "[{SwitchId}] Could not release inputs before disconnecting; they will be released on reconnect", _session.SwitchId);
            lock (_gate)
            {
                _pendingNeutralise = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var tracked in _held.Values)
            {
                tracked.Watchdog?.Dispose();
            }

            foreach (var tracked in _sticks.Values)
            {
                tracked.Watchdog?.Dispose();
            }
        }

        foreach (var semaphore in _buttonLocks.Values.Concat(_stickLocks.Values))
        {
            semaphore.Dispose();
        }
    }

    private async Task SendPressAsync(SwitchButton button, TimeSpan? watchdog, CancellationToken cancellationToken)
    {
        // Track before sending: if the send fails midway the Switch may have applied it.
        var token = Track(button, watchdog);
        try
        {
            await _session.ExecuteAsync((client, ct) => client.SendAsync(SysBotCommands.Press(button), ct), cancellationToken).ConfigureAwait(false);
        }
        catch (NxHubException ex) when (ex.Kind == ErrorKind.NotConnected)
        {
            // Nothing was sent.
            Untrack(button, token);
            throw;
        }
    }

    private async Task ReleaseTrackedAsync(SwitchButton button)
    {
        // Deliberately not linked to the caller's token: a cancelled press must still release.
        using var timeout = new CancellationTokenSource(ReleaseTimeout, _time);
        try
        {
            await _session.ExecuteAsync((client, ct) => client.SendAsync(SysBotCommands.Release(button), ct), timeout.Token).ConfigureAwait(false);
            Untrack(button);
        }
        catch (NxHubException ex) when (ex.Kind == ErrorKind.NotConnected)
        {
            MarkPendingIfTracked();
            throw;
        }
    }

    private async Task SendStickAsync(SwitchStick stick, StickPosition position, TimeSpan? watchdog, CancellationToken cancellationToken)
    {
        if (position.IsCentred)
        {
            await _session.ExecuteAsync((client, ct) => client.SendAsync(SysBotCommands.SetStick(stick, position), ct), cancellationToken).ConfigureAwait(false);
            UntrackStick(stick);
            return;
        }

        var token = TrackStick(stick, position, watchdog);
        try
        {
            await _session.ExecuteAsync((client, ct) => client.SendAsync(SysBotCommands.SetStick(stick, position), ct), cancellationToken).ConfigureAwait(false);
        }
        catch (NxHubException ex) when (ex.Kind == ErrorKind.NotConnected)
        {
            UntrackStick(stick, token);
            throw;
        }
    }

    private async Task CentreTrackedAsync(SwitchStick stick)
    {
        using var timeout = new CancellationTokenSource(ReleaseTimeout, _time);
        await _session.ExecuteAsync((client, ct) => client.SendAsync(SysBotCommands.SetStick(stick, StickPosition.Centre), ct), timeout.Token).ConfigureAwait(false);
        UntrackStick(stick);
    }

    // Tracking changes are published (outside the lock) so UIs can show what is held.
    private long Track(SwitchButton button, TimeSpan? watchdog)
    {
        long token;
        bool changed;
        lock (_gate)
        {
            token = ++_nextToken;
            var since = _time.GetUtcNow();
            changed = !_held.TryGetValue(button, out var existing);
            if (existing is not null)
            {
                existing.Watchdog?.Dispose();
                since = existing.Since;
            }

            var timer = watchdog is { } limit
                ? _time.CreateTimer(_ => OnButtonWatchdog(button, token), null, limit, Timeout.InfiniteTimeSpan)
                : null;
            _held[button] = new Tracked(token, timer, watchdog, since);
        }

        if (changed)
        {
            _session.PublishControllerState();
        }

        return token;
    }

    private void Untrack(SwitchButton button, long? token = null)
    {
        bool removed;
        lock (_gate)
        {
            removed = _held.TryGetValue(button, out var tracked) && (token is null || tracked.Token == token);
            if (removed)
            {
                tracked!.Watchdog?.Dispose();
                _held.Remove(button);
            }
        }

        if (removed)
        {
            _session.PublishControllerState();
        }
    }

    private long TrackStick(SwitchStick stick, StickPosition position, TimeSpan? watchdog)
    {
        long token;
        bool changed;
        lock (_gate)
        {
            token = ++_nextToken;
            changed = !_sticks.TryGetValue(stick, out var existing) || existing.Position != position;
            existing?.Watchdog?.Dispose();
            var timer = watchdog is { } limit
                ? _time.CreateTimer(_ => OnStickWatchdog(stick, token), null, limit, Timeout.InfiniteTimeSpan)
                : null;
            _sticks[stick] = new TrackedStick(token, position, timer, watchdog);
        }

        if (changed)
        {
            _session.PublishControllerState();
        }

        return token;
    }

    private void UntrackStick(SwitchStick stick, long? token = null)
    {
        bool removed;
        lock (_gate)
        {
            removed = _sticks.TryGetValue(stick, out var tracked) && (token is null || tracked.Token == token);
            if (removed)
            {
                tracked!.Watchdog?.Dispose();
                _sticks.Remove(stick);
            }
        }

        if (removed)
        {
            _session.PublishControllerState();
        }
    }

    private void MarkPendingIfTracked()
    {
        lock (_gate)
        {
            _pendingNeutralise = _held.Count > 0 || _sticks.Count > 0;
        }
    }

    private void ClearTrackingLocked()
    {
        foreach (var tracked in _held.Values)
        {
            tracked.Watchdog?.Dispose();
        }

        foreach (var tracked in _sticks.Values)
        {
            tracked.Watchdog?.Dispose();
        }

        _held.Clear();
        _sticks.Clear();
        _pendingNeutralise = false;
    }

    private void OnButtonWatchdog(SwitchButton button, long token)
    {
        TimeSpan? limit;
        lock (_gate)
        {
            if (!_held.TryGetValue(button, out var tracked) || tracked.Token != token)
            {
                return;
            }

            limit = tracked.Limit;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var buttonLock = _buttonLocks[button];
                await buttonLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    lock (_gate)
                    {
                        if (!_held.TryGetValue(button, out var current) || current.Token != token)
                        {
                            return;
                        }
                    }

                    await ReleaseTrackedAsync(button).ConfigureAwait(false);
                }
                finally
                {
                    buttonLock.Release();
                }

                _session.LogEvent(
                    ActivityLevel.Warning,
                    ActivityCategory.Controller,
                    $"Auto-released {button} after {limit?.TotalSeconds:0.#} s without a release (safety watchdog).");
            }
            catch (Exception ex) when (ex is NxHubException or ObjectDisposedException)
            {
                _logger.LogWarning(ex, "[{SwitchId}] Watchdog could not release {Button}", _session.SwitchId, button);
            }
        });
    }

    private void OnStickWatchdog(SwitchStick stick, long token)
    {
        lock (_gate)
        {
            if (!_sticks.TryGetValue(stick, out var tracked) || tracked.Token != token)
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var stickLock = _stickLocks[stick];
                await stickLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    lock (_gate)
                    {
                        if (!_sticks.TryGetValue(stick, out var current) || current.Token != token)
                        {
                            return;
                        }
                    }

                    await CentreTrackedAsync(stick).ConfigureAwait(false);
                }
                finally
                {
                    stickLock.Release();
                }

                _session.LogEvent(ActivityLevel.Warning, ActivityCategory.Controller, $"Auto-centred {stick} stick (safety watchdog).");
            }
            catch (Exception ex) when (ex is NxHubException or ObjectDisposedException)
            {
                _logger.LogWarning(ex, "[{SwitchId}] Watchdog could not centre {Stick} stick", _session.SwitchId, stick);
            }
        });
    }

    private static void ValidateRange(string field, TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min || value > max)
        {
            throw new ValidationFailedException(field, $"Must be between {min.TotalMilliseconds:0} and {max.TotalMilliseconds:0} ms.");
        }
    }

    private sealed record Tracked(long Token, ITimer? Watchdog, TimeSpan? Limit, DateTimeOffset Since);

    private sealed record TrackedStick(long Token, StickPosition Position, ITimer? Watchdog, TimeSpan? Limit);
}
