using System.Reflection;
using System.Xml.Linq;
using DiskMonitor.Events;
using DiskMonitor.Models;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class StorDiagEventTests
{
    private const string Channel = "Microsoft-Windows-Storage-ClassPnP/Operational";
    private const string Provider = "Microsoft-Windows-StorDiag";
    private const string DeviceSerial = "2009091700001639";

    [Theory]
    [InlineData(93884L, 36, 6, "12010000FF00")]
    [InlineData(93885L, 32, 16, "9E100000000000000000000000200000")]
    public void BootTimeNonReadWriteFailuresAreAttributedWithoutInventingRetries(
        long recordId,
        int additionalSenseCode,
        int cdbByteCount,
        string cdbBytes)
    {
        var bootTime = new DateTimeOffset(2026, 9, 20, 11, 50, 22, TimeSpan.Zero);
        var observation = Parse507(recordId, additionalSenseCode, cdbByteCount, cdbBytes, bootTime);

        Assert.NotNull(observation);
        Assert.Equal(Provider, observation.Source);
        Assert.Equal(Channel, observation.Channel);
        Assert.Equal(507, observation.EventId);
        Assert.Equal(recordId, observation.EventRecordId);
        Assert.Equal("storage.command-failed", observation.RuleId);
        Assert.Equal(ObservationCategory.Storage, observation.Category);
        Assert.Equal(ObservationSeverity.Warning, observation.Severity);
        Assert.Equal(AlertMode.Repeated, observation.AlertMode);
        Assert.Equal(5, observation.DiskNumber);
        Assert.Equal(DeviceSerial, observation.DeviceSerialNumber);
        Assert.Null(observation.DriveLetter); // The event carries a device number, not the eventual H: mount.
        Assert.False(observation.IsHistorical);
        Assert.DoesNotContain("retried", observation.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"CdbBytes={cdbBytes}", observation.Details, StringComparison.Ordinal);
        Assert.Contains("NumberOfRetriesDone=0", observation.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void BothBootRecordsRemainDistinctEvidence()
    {
        var bootTime = new DateTimeOffset(2026, 9, 20, 11, 50, 22, TimeSpan.Zero);
        var first = Parse507(93884, 36, 6, "12010000FF00", bootTime);
        var second = Parse507(93885, 32, 16, "9E100000000000000000000000200000", bootTime);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.EvidenceKey, second.EvidenceKey);
    }

    [Fact]
    public void ClassPnpChannelIsSubscribedAndItsQueryIncludesStorDiagErrors()
    {
        // The query alone cannot prove a watcher is created for the channel.
        var field = typeof(EventLogMonitor).GetField("Channels", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var channels = Assert.IsType<string[]>(field.GetValue(null));
        Assert.Contains(Channel, channels);

        var bootTime = new DateTimeOffset(2026, 9, 20, 11, 50, 22, TimeSpan.Zero);
        var query = EventLogMonitor.BuildQuery(Channel, bootTime, bootTime.AddMinutes(1));
        Assert.Contains($"<Select Path=\"{Channel}\">", query, StringComparison.Ordinal);
        Assert.Contains($"Provider[@Name='{Provider}']", query, StringComparison.Ordinal);
        Assert.Contains("Level=2", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Provider, 511)]
    [InlineData("Microsoft-Windows-Other", 507)]
    public void OtherEventIdsAndProvidersAreIgnored(string provider, int eventId)
    {
        var observation = EventObservationParser.ParseEvent(
            provider,
            eventId,
            2,
            93884,
            new DateTime(2026, 9, 20, 11, 50, 35, DateTimeKind.Utc),
            "Completing a failed non-ReadWrite SCSI SRB request",
            RealStorDiagXml(36, 6, "12010000FF00"),
            new DateTimeOffset(2026, 9, 20, 11, 50, 22, TimeSpan.Zero),
            Channel);

        Assert.Null(observation);
    }

    [Fact]
    public void OnlyEventsBeforeTheActualBootTimeAreHistorical()
    {
        var bootEventTime = new DateTimeOffset(2026, 9, 20, 11, 50, 22, TimeSpan.Zero);
        var fromCurrentBoot = Parse507(93884, 36, 6, "12010000FF00", bootEventTime);
        var beforeCutoff = Parse507(93884, 36, 6, "12010000FF00", bootEventTime.AddSeconds(14));

        Assert.NotNull(fromCurrentBoot);
        Assert.NotNull(beforeCutoff);
        Assert.False(fromCurrentBoot.IsHistorical);
        Assert.True(beforeCutoff.IsHistorical);
    }

    [Fact]
    public void ActualNotReadyWarningRetainsQualifierStatusAndExecutionContext()
    {
        var document = XDocument.Parse(RealStorDiagXml(4, 6, "000000000000"));
        var ns = document.Root!.Name.Namespace;
        var data = document.Descendants(ns + "Data").ToDictionary(element => element.Attribute("Name")!.Value);
        data["SrbStatus"].Value = "196";
        data["SenseKey"].Value = "2";
        data["DownLevelIrpStatus"].Value = "0xc0000185";
        document.Root.Element(ns + "System")!.Add(
            new XElement(ns + "Execution", new XAttribute("ProcessID", 5440), new XAttribute("ThreadID", 5336)),
            new XElement(ns + "Correlation", new XAttribute("ActivityID", "{f89d410c-0847-4525-bc00-f88e9607d79b}")));
        var xml = document.ToString();

        var observation = ParseXml(507, xml);

        Assert.NotNull(observation);
        Assert.Equal(ObservationSeverity.Warning, observation.Severity);
        Assert.Equal(AlertMode.Repeated, observation.AlertMode);
        Assert.Contains("TEST UNIT READY", observation.Details, StringComparison.Ordinal);
        Assert.Contains("NOT READY", observation.Details, StringComparison.Ordinal);
        Assert.Contains("cause not reportable", observation.Details, StringComparison.Ordinal);
        Assert.Contains("ASCQ=0x00", observation.Details, StringComparison.Ordinal);
        Assert.Contains("AdditionalSenseCodeQualifier=0", observation.Details, StringComparison.Ordinal);
        Assert.Contains("STATUS_IO_DEVICE_ERROR", observation.Details, StringComparison.Ordinal);
        Assert.Contains("autosense valid=True; queue frozen=True", observation.Details, StringComparison.Ordinal);
        Assert.Equal(5440, observation.EventProcessId);
        Assert.Equal(5336, observation.EventThreadId);
        Assert.Equal("{f89d410c-0847-4525-bc00-f88e9607d79b}", observation.EventActivityId);
        Assert.Equal(xml, observation.RawEventXml);
        Assert.False(observation.RawEventXmlTruncated);
        Assert.DoesNotContain("unverified", observation.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(500, "storage.read-failed")]
    [InlineData(501, "storage.write-failed")]
    [InlineData(502, "storage.read-failed")]
    [InlineData(503, "storage.write-failed")]
    [InlineData(505, "storage.read-failed")]
    [InlineData(506, "storage.write-failed")]
    public void RelatedDataFailuresRemainSeparateFromProbeWarnings(int eventId, string ruleId)
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <EventData>
                <Data Name="DeviceNumber">6</Data>
                <Data Name="SerialNumber">2009091700001639</Data>
                <Data Name="IrpStatus">0xc00000a3</Data>
                <Data Name="LBA">0x0</Data>
                <Data Name="TransferByteCount">8192</Data>
              </EventData>
            </Event>
            """;

        var observation = ParseXml(eventId, xml);

        Assert.NotNull(observation);
        Assert.Equal(ruleId, observation.RuleId);
        Assert.Equal(6, observation.DiskNumber);
        Assert.Equal(DeviceSerial, observation.DeviceSerialNumber);
        Assert.Equal(ObservationSeverity.Error, observation.Severity);
        Assert.Equal(AlertMode.Immediate, observation.AlertMode);
        Assert.Contains("STATUS_DEVICE_NOT_READY", observation.Details, StringComparison.Ordinal);
        Assert.Contains("TransferByteCount=8192", observation.Details, StringComparison.Ordinal);
        Assert.Contains("LBA=0x0", observation.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(504, "storage.ioctl-failed")]
    [InlineData(508, "storage.non-scsi-failed")]
    [InlineData(509, "storage.pnp-failed")]
    [InlineData(510, "storage.power-failed")]
    [InlineData(523, "storage.capacity-failed")]
    public void RelatedControlAndPowerFailuresAreRepeatedWarnings(int eventId, string ruleId)
    {
        var observation = ParseXml(eventId, RealStorDiagXml(4, 6, "000000000000"));

        Assert.NotNull(observation);
        Assert.Equal(ruleId, observation.RuleId);
        Assert.Equal(ObservationSeverity.Warning, observation.Severity);
        Assert.Equal(AlertMode.Repeated, observation.AlertMode);
        Assert.Contains("DeviceGUID=", observation.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedXmlDoesNotPreventAlertNormalization()
    {
        var observation = ParseXml(507, "<Event>");

        Assert.NotNull(observation);
        Assert.Null(observation.EventProcessId);
        Assert.Equal("<Event>", observation.RawEventXml);
    }

    [Fact]
    public void OversizedDiagnosticXmlIsExplicitlyMarkedAsTruncated()
    {
        var xml = $"<Event><!--{new string('x', 70 * 1024)}--></Event>";

        var observation = ParseXml(507, xml);

        Assert.NotNull(observation);
        Assert.True(observation.RawEventXmlTruncated);
        Assert.Equal(64 * 1024, observation.RawEventXml.Length);
    }

    private static StorageObservation? ParseXml(int eventId, string xml) =>
        EventObservationParser.ParseEvent(Provider, eventId, 2, 135400, DateTime.UtcNow,
            "Storage command failed", xml, channel: Channel);

    private static StorageObservation? Parse507(
        long recordId,
        int additionalSenseCode,
        int cdbByteCount,
        string cdbBytes,
        DateTimeOffset bootTime) =>
        EventObservationParser.ParseEvent(
            Provider,
            507,
            2,
            recordId,
            new DateTime(2026, 9, 20, 11, 50, 35, DateTimeKind.Utc),
            "Completing a failed non-ReadWrite SCSI SRB request",
            RealStorDiagXml(additionalSenseCode, cdbByteCount, cdbBytes),
            bootTime,
            Channel);

    private static string RealStorDiagXml(int additionalSenseCode, int cdbByteCount, string cdbBytes) =>
        $$"""
         <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
           <System>
             <Provider Name="Microsoft-Windows-StorDiag" />
             <EventID>507</EventID>
             <Level>2</Level>
             <Channel>Microsoft-Windows-Storage-ClassPnP/Operational</Channel>
           </System>
           <EventData>
             <Data Name="DeviceGUID">{da2c245b-4ec1-4056-cc30-11245d7ff83f}</Data>
             <Data Name="DeviceNumber">5</Data>
             <Data Name="Vendor">Ext Hard</Data>
             <Data Name="Model"> Disk</Data>
             <Data Name="SerialNumber">2009091700001639</Data>
             <Data Name="DownLevelIrpStatus">0xc000000d</Data>
             <Data Name="SrbStatus">132</Data>
             <Data Name="ScsiStatus">2</Data>
             <Data Name="SenseKey">5</Data>
             <Data Name="AdditionalSenseCode">{{additionalSenseCode}}</Data>
             <Data Name="AdditionalSenseCodeQualifier">0</Data>
             <Data Name="CdbByteCount">{{cdbByteCount}}</Data>
             <Data Name="CdbBytes">{{cdbBytes}}</Data>
             <Data Name="NumberOfRetriesDone">0</Data>
           </EventData>
         </Event>
         """;
}
