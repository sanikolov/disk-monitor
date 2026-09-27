using DiskMonitor.Monitoring;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class RefreshAttemptGateTests
{
    [Fact]
    public async Task HungProbeDoesNotBlockTheHeartbeatOrStartOverlappingProbes()
    {
        var gate = new RefreshAttemptGate();
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        Task Start()
        {
            starts++;
            return stalled.Task;
        }

        var first = await gate.RunAsync(Start, TimeSpan.FromMilliseconds(10), CancellationToken.None);
        var next = await gate.RunAsync(Start, TimeSpan.FromMilliseconds(10), CancellationToken.None);

        Assert.Equal(RefreshAttemptOutcome.TimedOut, first);
        Assert.Equal(RefreshAttemptOutcome.StillRunning, next);
        Assert.Equal(1, starts);

        stalled.SetResult();
        var recovered = await gate.RunAsync(
            () =>
            {
                starts++;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(RefreshAttemptOutcome.Completed, recovered);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task AFailedProbeDoesNotPreventTheNextAttempt()
    {
        var gate = new RefreshAttemptGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunAsync(() => Task.FromException(new InvalidOperationException("WMI failed")),
                TimeSpan.FromSeconds(1), CancellationToken.None));

        var outcome = await gate.RunAsync(() => Task.CompletedTask, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(RefreshAttemptOutcome.Completed, outcome);
    }
}
