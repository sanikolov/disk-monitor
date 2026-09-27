namespace DiskMonitor.Monitoring;

internal enum RefreshAttemptOutcome
{
    Completed,
    TimedOut,
    StillRunning
}

internal sealed class RefreshAttemptGate
{
    private Task? _inFlight;

    public async Task<RefreshAttemptOutcome> RunAsync(
        Func<Task> startRefresh,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // A synchronous WMI or device IOCTL call may ignore cancellation. Never
        // start another copy while the previous read still owns its worker thread.
        if (_inFlight is { IsCompleted: false })
        {
            return RefreshAttemptOutcome.StillRunning;
        }

        _inFlight = startRefresh();
        try
        {
            await _inFlight.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return RefreshAttemptOutcome.Completed;
        }
        catch (TimeoutException)
        {
            return RefreshAttemptOutcome.TimedOut;
        }
    }
}
