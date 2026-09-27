using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;

namespace DiskMonitor.Infrastructure;

internal static class SystemBootTime
{
    public static DateTimeOffset GetUtc()
    {
        var uptimeEstimate = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        try
        {
            var query = new EventLogQuery(
                "System",
                PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]")
            {
                ReverseDirection = true
            };
            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent();
            if (record is null)
            {
                return uptimeEstimate;
            }

            var document = XDocument.Parse(record.ToXml());
            var startTime = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Data" &&
                                           element.Attribute("Name")?.Value == "StartTime")?.Value;
            if (DateTimeOffset.TryParse(startTime, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var eventBootTime))
            {
                return ChooseBootTime(uptimeEstimate, eventBootTime);
            }
        }
        catch (Exception)
        {
            // The uptime estimate is still useful if the System log is inaccessible.
        }

        return uptimeEstimate;
    }

    internal static DateTimeOffset ChooseBootTime(DateTimeOffset uptimeEstimate, DateTimeOffset eventBootTime)
    {
        // Reject a stale Event 12 after log rotation, but prefer its precise OS
        // StartTime when it belongs to the same boot as the uptime estimate.
        var difference = (eventBootTime - uptimeEstimate).Duration();
        return difference <= TimeSpan.FromMinutes(10) ? eventBootTime : uptimeEstimate;
    }
}
