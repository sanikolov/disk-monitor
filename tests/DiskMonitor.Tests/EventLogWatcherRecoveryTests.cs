using System.Diagnostics.Eventing.Reader;
using DiskMonitor.Events;
using DiskMonitor.Infrastructure;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class EventLogWatcherRecoveryTests
{
    [Theory]
    [InlineData(15011)] // ERROR_EVT_QUERY_RESULT_STALE: log cleared or rolled over.
    [InlineData(15012)] // ERROR_EVT_QUERY_RESULT_INVALID_POSITION.
    public void StaleBookmarkErrorsCanTriggerBootLimitedReplay(int nativeError)
    {
        Assert.True(EventLogMonitor.IsStaleBookmarkError(new ReadingError(nativeError)));
        Assert.True(EventLogMonitor.IsStaleBookmarkError(new GenericEventLogError(nativeError)));
    }

    [Fact]
    public void UnrelatedSubscriptionErrorsMustNotDiscardTheBookmark()
    {
        Assert.False(EventLogMonitor.IsStaleBookmarkError(new ReadingError(5)));
        Assert.False(EventLogMonitor.IsStaleBookmarkError(new UnauthorizedAccessException()));
    }

    [Fact]
    public void AWatcherIsMissingUntilItsSubscriptionIsEnabled()
    {
        using var monitor = new EventLogMonitor(
            new MonitorLogger(Path.Combine(AppContext.BaseDirectory, "watcher-recovery-test.log")),
            DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Contains("System", monitor.GetMissingRequiredChannels());
        Assert.Contains("Microsoft-Windows-Ntfs/Operational", monitor.GetMissingRequiredChannels());
        Assert.Contains("Microsoft-Windows-Storage-ClassPnP/Operational", monitor.GetMissingRequiredChannels());
    }

    private sealed class ReadingError : EventLogReadingException
    {
        public ReadingError(int nativeError) : base("Simulated Windows Event Log failure")
        {
            HResult = unchecked((int)(0x80070000u | (uint)nativeError));
        }
    }

    private sealed class GenericEventLogError : EventLogException
    {
        public GenericEventLogError(int nativeError) : base("Simulated Windows Event Log failure")
        {
            HResult = unchecked((int)(0x80070000u | (uint)nativeError));
        }
    }
}
