using Microsoft.Extensions.Logging;
using NxHub.Core.Activity;
using NxHub.Core.Errors;
using NxHub.Core.Events;
using NxHub.Core.Sessions;
using NxHub.Protocol;

namespace NxHub.Core.Macros;

public enum MacroRunStatus
{
    Running,
    Paused,
    Completed,
    Stopped,
    Failed,
}

/// <summary>Progress of the current (or last) macro run on one Switch.</summary>
public sealed record MacroRunState
{
    public required string RunId { get; init; }

    public string? MacroId { get; init; }

    public required string MacroName { get; init; }

    public required MacroRunStatus Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>1-based pass through the whole macro.</summary>
    public int Iteration { get; init; }

    /// <summary>Total passes, or null when repeating until stopped.</summary>
    public int? Iterations { get; init; }

    public int StepsExecuted { get; init; }

    public string? CurrentStep { get; init; }

    public string? Error { get; init; }

    public bool IsActive => Status is MacroRunStatus.Running or MacroRunStatus.Paused;
}

/// <summary>
/// Runs one macro at a time on a Switch, through the same <see cref="SwitchController"/> as manual
/// input, so every safety rule (watchdogs, release on failure) applies. Whatever the outcome, held
/// inputs are released when the run ends.
/// </summary>
public sealed class MacroRunner : IDisposable
{
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(150);

    private readonly SwitchSession _session;
    private readonly IEventPublisher _events;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private MacroRunState? _state;
    private CancellationTokenSource? _stop;
    private TaskCompletionSource? _resume;
    private Task? _run;
    private DateTimeOffset _lastPublished;

    internal MacroRunner(SwitchSession session, IEventPublisher events, TimeProvider time, ILogger logger)
    {
        _session = session;
        _events = events;
        _time = time;
        _logger = logger;
    }

    public MacroRunState? State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Starts a macro. Fails if one is already running on this Switch or it is not connected.</summary>
    public MacroRunState Start(Macro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        if (_session.State != ConnectionState.Connected)
        {
            throw Errors.Errors.NotConnected(_session.Device.Name);
        }

        MacroRunState state;
        lock (_gate)
        {
            if (_state is { IsActive: true } running)
            {
                throw new NxHubException(ErrorKind.Conflict, "macro_running", $"'{running.MacroName}' is already running on this Switch. Stop it first.");
            }

            _stop = new CancellationTokenSource();
            _resume = null;
            state = new MacroRunState
            {
                RunId = "run_" + Guid.NewGuid().ToString("N")[..10],
                MacroId = macro.Id,
                MacroName = macro.Name,
                Status = MacroRunStatus.Running,
                StartedAt = _time.GetUtcNow(),
                Iteration = 1,
                Iterations = macro.Repeat == 0 ? null : macro.Repeat,
            };
            _state = state;
            var token = _stop.Token;
            _run = Task.Run(() => RunAsync(macro, token), CancellationToken.None);
        }

        Publish(force: true);
        _session.LogEvent(ActivityLevel.Info, ActivityCategory.Controller, $"Macro '{macro.Name}' started.");
        return state;
    }

    public MacroRunState Pause()
    {
        lock (_gate)
        {
            if (_state is not { Status: MacroRunStatus.Running })
            {
                throw NotRunning();
            }

            _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _state = _state with { Status = MacroRunStatus.Paused };
        }

        Publish(force: true);
        return State!;
    }

    public MacroRunState Resume()
    {
        TaskCompletionSource? resume;
        lock (_gate)
        {
            if (_state is not { Status: MacroRunStatus.Paused })
            {
                throw NotRunning();
            }

            resume = _resume;
            _resume = null;
            _state = _state with { Status = MacroRunStatus.Running };
        }

        resume?.TrySetResult();
        Publish(force: true);
        return State!;
    }

    /// <summary>Stops the current run (if any) and waits until its inputs have been released.</summary>
    public async Task<MacroRunState?> StopAsync()
    {
        Task? run;
        lock (_gate)
        {
            if (_state is not { IsActive: true })
            {
                return _state;
            }

            _stop?.Cancel();
            _resume?.TrySetResult();
            run = _run;
        }

        if (run is not null)
        {
            await run.ConfigureAwait(false);
        }

        return State;
    }

    private async Task RunAsync(Macro macro, CancellationToken token)
    {
        MacroRunStatus outcome;
        string? error = null;
        try
        {
            for (var iteration = 1; macro.Repeat == 0 || iteration <= macro.Repeat; iteration++)
            {
                Update(s => s with { Iteration = iteration });
                var started = _time.GetTimestamp();
                await RunStepsAsync(macro.Steps, token).ConfigureAwait(false);

                // Guard against a tight loop if a pass finishes almost instantly.
                if (_time.GetElapsedTime(started) < TimeSpan.FromMilliseconds(10))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), _time, token).ConfigureAwait(false);
                }
            }

            outcome = MacroRunStatus.Completed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            outcome = MacroRunStatus.Stopped;
        }
        catch (NxHubException ex)
        {
            outcome = MacroRunStatus.Failed;
            error = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{SwitchId}] Macro '{Macro}' crashed", _session.SwitchId, macro.Name);
            outcome = MacroRunStatus.Failed;
            error = "Unexpected error; see the server log.";
        }

        // Never leave anything the macro pressed held down.
        try
        {
            if (_session.State == ConnectionState.Connected)
            {
                await _session.Controller.ReleaseAllAsync(everything: false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (NxHubException ex)
        {
            _logger.LogWarning(ex, "[{SwitchId}] Could not release inputs after macro", _session.SwitchId);
        }

        lock (_gate)
        {
            _state = _state! with { Status = outcome, EndedAt = _time.GetUtcNow(), Error = error, CurrentStep = null };
            _stop?.Dispose();
            _stop = null;
            _resume = null;
        }

        Publish(force: true);
        var message = outcome switch
        {
            MacroRunStatus.Completed => $"Macro '{macro.Name}' finished.",
            MacroRunStatus.Stopped => $"Macro '{macro.Name}' stopped.",
            _ => $"Macro '{macro.Name}' failed: {error}",
        };
        _session.LogEvent(outcome == MacroRunStatus.Failed ? ActivityLevel.Error : ActivityLevel.Info, ActivityCategory.Controller, message);
    }

    private async Task RunStepsAsync(IReadOnlyList<MacroStep> steps, CancellationToken token)
    {
        var controller = _session.Controller;
        var maxHold = controller.Options.MaxHold;
        foreach (var step in steps)
        {
            await WhilePausedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Update(s => s with { CurrentStep = step.Describe() });

            switch (step.Type)
            {
                case MacroStepType.Press:
                    await controller.PressAsync(step.Button!.Value, TimeSpan.FromMilliseconds(step.DurationMs ?? MacroRules.DefaultPressMs), token, logActivity: false).ConfigureAwait(false);
                    break;
                case MacroStepType.Hold:
                    await controller.HoldAsync(step.Button!.Value, maxHold, token, logActivity: false).ConfigureAwait(false);
                    break;
                case MacroStepType.Release:
                    await controller.ReleaseAsync(step.Button!.Value, token, logActivity: false).ConfigureAwait(false);
                    break;
                case MacroStepType.Stick:
                    await controller.SetStickAsync(
                        step.Stick!.Value,
                        StickPosition.FromNormalised(step.X ?? 0, step.Y ?? 0),
                        step.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                        maxHold,
                        token).ConfigureAwait(false);
                    break;
                case MacroStepType.Wait:
                    await Task.Delay(TimeSpan.FromMilliseconds(step.DurationMs ?? 0), _time, token).ConfigureAwait(false);
                    break;
                case MacroStepType.Repeat:
                    for (var i = 0; i < step.Count; i++)
                    {
                        await RunStepsAsync(step.Steps!, token).ConfigureAwait(false);
                    }

                    break;
            }

            if (step.Type != MacroStepType.Repeat)
            {
                Update(s => s with { StepsExecuted = s.StepsExecuted + 1 });
            }
        }
    }

    private async Task WhilePausedAsync(CancellationToken token)
    {
        Task? resume;
        lock (_gate)
        {
            resume = _resume?.Task;
        }

        if (resume is not null)
        {
            await resume.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private void Update(Func<MacroRunState, MacroRunState> change)
    {
        lock (_gate)
        {
            _state = change(_state!);
        }

        Publish(force: false);
    }

    private void Publish(bool force)
    {
        MacroRunState? state;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!force && now - _lastPublished < PublishInterval)
            {
                return;
            }

            _lastPublished = now;
            state = _state;
        }

        _events.Publish(new NxHubEvent(EventTypes.MacroState, _session.SwitchId, _time.GetUtcNow(), state));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stop?.Dispose();
            _stop = null;
        }
    }

    private static NxHubException NotRunning() =>
        new(ErrorKind.Conflict, "macro_not_running", "No macro is running on this Switch.");
}
