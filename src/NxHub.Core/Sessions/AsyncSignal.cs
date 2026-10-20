namespace NxHub.Core.Sessions;

/// <summary>An auto-reset signal that can be awaited with a timeout driven by a <see cref="TimeProvider"/>.</summary>
internal sealed class AsyncSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _tcs = NewSource();
    private bool _set;

    public void Set()
    {
        TaskCompletionSource toComplete;
        lock (_gate)
        {
            _set = true;
            toComplete = _tcs;
        }

        toComplete.TrySetResult();
    }

    /// <summary>Waits until signalled or the timeout elapses. Returns true if signalled.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        Task waitTask;
        lock (_gate)
        {
            if (_set)
            {
                _set = false;
                return true;
            }

            if (_tcs.Task.IsCompleted)
            {
                _tcs = NewSource();
            }

            waitTask = _tcs.Task;
        }

        if (timeout == Timeout.InfiniteTimeSpan)
        {
            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                await waitTask.WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        lock (_gate)
        {
            _set = false;
        }

        return true;
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
