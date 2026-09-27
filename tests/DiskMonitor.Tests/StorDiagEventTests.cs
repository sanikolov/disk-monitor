using System.Reflection;
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
    [InlineData(Provider, 506)]
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
