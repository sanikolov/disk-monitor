using System.Reflection;
using DiskMonitor.Events;
using DiskMonitor.Infrastructure;
using DiskMonitor.Models;
using DiskMonitor.Monitoring;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class NtfsSlowIoTests
{
    private const string EventXml = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <EventData>
            <Data Name="VolumeCorrelationId">{05bf81a9-88b7-44bb-a9e2-bc55685b0b4b}</Data>
            <Data Name="VolumeName">D:</Data>
            <Data Name="MaxLatencyMs">30000</Data>
            <Data Name="ProcessId">4</Data>
            <Data Name="ProcessName">System</Data>
            <Data Name="FileName">\DockerDesktopStorage\wsl\disk\docker_data.vhdx</Data>
            <Data Name="IoTypeStr">Write: NonPaging, NonCached, Async</Data>
            <Data Name="LatencyMs">34880</Data>
            <Data Name="DeviceGuid">{a9b7709e-1252-69db-6bd3-cf54e742d6c0}</Data>
            <Data Name="ProductId">ST8000DM004-2CX188</Data>
            <Data Name="DeviceSerialNumber">            ZCT259HY</Data>
            <Data Name="BusType">11</Data>
          </EventData>
        </Event>
        """;

    private const string EventDetails = """
        An IO took more than 30000 ms to complete:
        File name: \DockerDesktopStorage\wsl\disk\docker_data.vhdx
        Latency: 34880 ms
        Volume name: D:
        Device model: ST8000DM004-2CX188
        Device serial number: ZCT259HY
        Bus type: SATA
        """;

    [Fact]
    public void Ntfs147IdentifiesSlowIoOnSataDriveD()
    {
        var observation = Parse(82319, DateTime.Now);

        Assert.NotNull(observation);
        Assert.Equal("ntfs.slow-io", observation!.RuleId);
        Assert.Equal(ObservationCategory.Ntfs, observation.Category);
        Assert.Equal(ObservationSeverity.Warning, observation.Severity);
        Assert.Equal(AlertMode.Repeated, observation.AlertMode);
        Assert.Equal("D:", observation.DriveLetter);
        Assert.Null(observation.DiskNumber);
        Assert.Equal("Microsoft-Windows-Ntfs/Operational", observation.Channel);
        Assert.Contains("34880 ms", observation.Details, StringComparison.Ordinal);
        Assert.Contains("ST8000DM004-2CX188", observation.Details, StringComparison.Ordinal);
        Assert.Contains("SATA", observation.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void NtfsOperationalWatcherIncludesLevelThreeSlowIoEvents()
    {
        const string channel = "Microsoft-Windows-Ntfs/Operational";
        var channelsField = typeof(EventLogMonitor).GetField("Channels", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(channelsField);
        Assert.Contains(channel, Assert.IsType<string[]>(channelsField.GetValue(null)));

        var bootTime = new DateTimeOffset(2026, 9, 20, 11, 50, 22, 500, TimeSpan.Zero);
        var query = EventLogMonitor.BuildQuery(channel, bootTime, bootTime.AddDays(2));
        Assert.Contains($"<Select Path=\"{channel}\">", query, StringComparison.Ordinal);
        Assert.Contains("Provider[@Name='Microsoft-Windows-Ntfs']", query, StringComparison.Ordinal);
        Assert.Contains("Level=3", query, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedNtfs147EventsAlertOnSataDriveWithoutUsbRequirement()
    {
        using var engine = new IncidentEngine(
            new InMemoryStateStore(),
            new MonitorLogger(Path.Combine(AppContext.BaseDirectory, "ntfs-slow-io-test.log")),
            new MonitorOptions { RepeatedEventThreshold = 2 });
        var alerts = new List<Incident>();
        engine.AlertRaised += (_, args) => alerts.Add(args.Incident);
        var disk = new DeviceSnapshot
        {
            DiskNumber = 1,
            Key = "serial:st8000dm004-2cx188|zct259hy|sata",
            Model = "ST8000DM004-2CX188",
            SerialNumber = "ZCT259HY",
            BusType = "SATA",
            DriveLetters = new[] { "D:" }
        };
        var now = DateTime.Now;
        var first = Parse(82318, now.AddSeconds(-30));
        var second = Parse(82319, now);

        Assert.NotNull(first);
        Assert.NotNull(second);
        engine.Process(first!, disk);
        engine.Process(first!, disk);
        Assert.Empty(alerts);
        engine.Process(second!, disk);

        var incident = Assert.Single(engine.GetIncidents());
        Assert.Single(alerts);
        Assert.Equal(2, incident.EvidenceCount);
        Assert.Equal(ObservationCategory.Ntfs, incident.Category);
        Assert.Contains("[SATA] (D:)", incident.DeviceName, StringComparison.Ordinal);
    }

    private static StorageObservation? Parse(long recordId, DateTime timestamp) =>
        EventObservationParser.ParseEvent(
            "Microsoft-Windows-Ntfs",
            147,
            3,
            recordId,
            timestamp,
            EventDetails,
            EventXml,
            channel: "Microsoft-Windows-Ntfs/Operational");

    private sealed class InMemoryStateStore : IMonitorStateStore
    {
        private MonitorState _state = new();

        public MonitorState Load() => _state;

        public void Save(MonitorState state) => _state = state;
    }
}
