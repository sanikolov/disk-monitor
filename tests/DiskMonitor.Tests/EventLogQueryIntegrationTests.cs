using System.Diagnostics.Eventing.Reader;
using DiskMonitor.Events;
using DiskMonitor.Infrastructure;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class EventLogQueryIntegrationTests
{
    [Theory]
    [InlineData("System")]
    [InlineData("Microsoft-Windows-Ntfs/Operational")]
    [InlineData("Microsoft-Windows-Storage-ClassPnP/Operational")]
    public void GeneratedWatcherQueryIsAcceptedByWindowsEventLog(string channel)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // String assertions cannot catch Windows Event Log XPath complexity limits.
        // The NTFS and ClassPnP channels carry the actual D: and H: records.
        if (channel != "System")
        {
            try
            {
                _ = EventLogSession.GlobalSession.GetLogInformation(channel, PathType.LogName);
            }
            catch (EventLogNotFoundException)
            {
                return; // Optional on older supported Windows installations.
            }
        }

        var now = DateTimeOffset.UtcNow;
        var xml = EventLogMonitor.BuildQuery(channel, now.AddMinutes(-1), now);
        var query = new EventLogQuery(channel, PathType.LogName, xml);
        using var reader = new EventLogReader(query);
        using var record = reader.ReadEvent();
    }

    [Theory]
    [InlineData("Microsoft-Windows-Ntfs/Operational", "Microsoft-Windows-Ntfs", 147)]
    [InlineData("Microsoft-Windows-Storage-ClassPnP/Operational", "Microsoft-Windows-StorDiag", 507)]
    public void ExistingCurrentBootDiskEventIsSelectedByWatcherQuery(string channel, string provider, int eventId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _ = EventLogSession.GlobalSession.GetLogInformation(channel, PathType.LogName);
        }
        catch (EventLogNotFoundException)
        {
            return;
        }

        var boot = SystemBootTime.GetUtc();
        var sampleQuery = new EventLogQuery(
            channel,
            PathType.LogName,
            $"*[System[Provider[@Name='{provider}'] and EventID={eventId}]]")
        {
            ReverseDirection = true
        };
        using var sampleReader = new EventLogReader(sampleQuery);
        using var sample = sampleReader.ReadEvent();
        if (sample?.TimeCreated is not { } eventTime || eventTime.ToUniversalTime() < boot.UtcDateTime)
        {
            return; // This host has no matching current-boot record to compare.
        }

        var query = new EventLogQuery(
            channel,
            PathType.LogName,
            EventLogMonitor.BuildQuery(channel, boot, DateTimeOffset.UtcNow))
        {
            ReverseDirection = true
        };
        using var reader = new EventLogReader(query);
        EventRecord? record;
        while ((record = reader.ReadEvent()) is not null)
        {
            using (record)
            {
                if (record.RecordId == sample.RecordId)
                {
                    return;
                }
            }
        }

        Assert.Fail($"The {provider} event {eventId} record {sample.RecordId} was omitted by the watcher query.");
    }
}
