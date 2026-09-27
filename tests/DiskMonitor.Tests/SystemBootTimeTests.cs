using DiskMonitor.Infrastructure;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class SystemBootTimeTests
{
    [Fact]
    public void KernelGeneralStartTimeKeepsBootTimeStorageErrorsInScope()
    {
        var uptimeEstimate = new DateTimeOffset(2026, 9, 20, 11, 50, 37, TimeSpan.Zero);
        var osStartTime = new DateTimeOffset(2026, 9, 20, 11, 50, 22, 500, TimeSpan.Zero);
        var hDriveEvent = new DateTimeOffset(2026, 9, 20, 11, 50, 35, TimeSpan.Zero);

        var cutoff = SystemBootTime.ChooseBootTime(uptimeEstimate, osStartTime);

        Assert.Equal(osStartTime, cutoff);
        Assert.True(hDriveEvent >= cutoff);
    }

    [Fact]
    public void AnOldEventLogRecordDoesNotReplaceTheCurrentUptimeEstimate()
    {
        var uptimeEstimate = new DateTimeOffset(2026, 9, 20, 11, 50, 37, TimeSpan.Zero);
        var oldStartTime = uptimeEstimate.AddDays(-7);

        Assert.Equal(uptimeEstimate, SystemBootTime.ChooseBootTime(uptimeEstimate, oldStartTime));
    }
}
