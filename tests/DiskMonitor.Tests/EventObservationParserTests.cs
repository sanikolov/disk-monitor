using DiskMonitor.Events;
using DiskMonitor.Models;
using DiskMonitor.Storage;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class EventObservationParserTests
{
    private static readonly string[] ExpectedOperationalStatuses = { "OK", "Predictive failure", "Lost communication" };

    [Fact]
    public void LocalEventTimestampIsConvertedToUtc()
    {
        var localTimestamp = DateTime.Now.AddSeconds(-30);

        var observation = EventObservationParser.ParseEvent(
            "disk",
            153,
            3,
            42,
            localTimestamp,
            "The IO operation at logical block address 123 for Disk 2 was retried.",
            string.Empty);

        Assert.NotNull(observation);
        Assert.Equal(new DateTimeOffset(localTimestamp).ToUniversalTime(), observation!.Timestamp);
        Assert.False(observation.IsHistorical);
    }

    [Fact]
    public void RaidPortIsExtractedForControllerCorrelation()
    {
        var observation = EventObservationParser.ParseEvent(
            "storport",
            129,
            2,
            43,
            DateTime.Now,
            "Reset to device, \\Device\\RaidPort3, was issued.",
            string.Empty);

        Assert.NotNull(observation);
        Assert.Equal(3, observation!.RaidPortNumber);
    }

    [Fact]
    public void NonStorageKernelPnpEventIsIgnored()
    {
        var observation = EventObservationParser.ParseEvent(
            "Microsoft-Windows-Kernel-PnP",
            219,
            2,
            44,
            DateTime.Now,
            "A driver failed to load.",
            EventXml(("DeviceInstanceId", "USB\\VID_1234&PID_5678"), ("ClassGuid", "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}")));

        Assert.Null(observation);
    }

    [Fact]
    public void StorageKernelPnpEventIsAccepted()
    {
        var observation = EventObservationParser.ParseEvent(
            "Microsoft-Windows-Kernel-PnP",
            219,
            2,
            45,
            DateTime.Now,
            "A USB storage driver failed to load.",
            EventXml(("DeviceInstanceId", "USBSTOR\\Disk_Ven_Test_Prod_Disk")));

        Assert.NotNull(observation);
        Assert.Equal(ObservationCategory.Usb, observation!.Category);
    }

    [Fact]
    public void RealKernelPnpWarningPayloadIsAcceptedAndIdentified()
    {
        const string instanceId = @"USB\VID_090C&PID_1000\AA30000000000048";
        var observation = EventObservationParser.ParseEvent(
            "Microsoft-Windows-Kernel-PnP",
            219,
            3,
            46,
            DateTime.Now,
            @"The driver \Driver\USBSTOR failed to load for the device USB\VID_090C&PID_1000\AA30000000000048.",
            EventXml(("DriverName", instanceId), ("FailureName", @"\Driver\USBSTOR")));

        Assert.NotNull(observation);
        Assert.Equal(ObservationSeverity.Warning, observation!.Severity);
        Assert.Equal(instanceId, observation.DeviceToken);
    }

    [Fact]
    public void NestedUserDataStoragePayloadIsAccepted()
    {
        const string instanceId = @"USBSTOR\DISK&VEN_TEST\123";
        var xml = $"""
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <UserData>
                <DeviceFailure xmlns="urn:test">
                  <DeviceInstanceId>{System.Security.SecurityElement.Escape(instanceId)}</DeviceInstanceId>
                </DeviceFailure>
              </UserData>
            </Event>
            """;

        var observation = EventObservationParser.ParseEvent(
            "Microsoft-Windows-Kernel-PnP",
            219,
            3,
            47,
            DateTime.Now,
            "A storage device driver failed to load.",
            xml);

        Assert.NotNull(observation);
        Assert.Equal(instanceId, observation!.DeviceToken);
    }

    [Fact]
    public void NonActionableNtfs98EventIsIgnored()
    {
        var observation = EventObservationParser.ParseEvent(
            "Ntfs",
            98,
            3,
            46,
            DateTime.Now,
            "The volume was checked and is healthy.",
            EventXml(("DriveName", "C:"), ("CorruptionActionState", "0")));

        Assert.Null(observation);
    }

    [Fact]
    public void ActionableNtfs98EventIsAccepted()
    {
        var observation = EventObservationParser.ParseEvent(
            "Ntfs",
            98,
            3,
            47,
            DateTime.Now,
            "The volume E: needs to be taken offline to perform a full chkdsk.",
            EventXml(("DriveName", "E:"), ("CorruptionActionState", "2")));

        Assert.NotNull(observation);
        Assert.Equal(ObservationCategory.Ntfs, observation!.Category);
    }

    [Fact]
    public void Ntfs98InformationLevelIsIncludedInWatcherQuery()
    {
        var bootTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var queryTime = bootTime.AddDays(10);

        var query = EventLogMonitor.BuildQuery("System", bootTime, queryTime);

        Assert.Contains("Level=4 and EventID=98", query, StringComparison.Ordinal);
        Assert.Contains("timediff(@SystemTime) &lt;= 864060000", query, StringComparison.Ordinal);
    }

    [Fact]
    public void EventBeforeBootIsMarkedHistorical()
    {
        var bootTime = DateTimeOffset.UtcNow.AddHours(-1);

        var observation = EventObservationParser.ParseEvent(
            "disk",
            153,
            3,
            49,
            bootTime.AddSeconds(-1).UtcDateTime,
            "The disk I/O operation was retried.",
            string.Empty,
            bootTime);

        Assert.NotNull(observation);
        Assert.True(observation!.IsHistorical);
    }

    [Fact]
    public void NtfsDelayedWriteFailureIsCriticalBecauseDataMayBeLost()
    {
        var observation = EventObservationParser.ParseEvent(
            "Ntfs",
            50,
            2,
            48,
            DateTime.Now,
            "Windows was unable to save all the data for J:.",
            EventXml(("DriveName", "J:")));

        Assert.NotNull(observation);
        Assert.Equal(ObservationSeverity.Critical, observation!.Severity);
        Assert.Equal(AlertMode.Immediate, observation.AlertMode);
    }

    [Fact]
    public void UaspStorResetIsAccepted()
    {
        var observation = EventObservationParser.ParseEvent(
            "UASPStor",
            129,
            3,
            49,
            DateTime.Now,
            @"Reset to device, \Device\RaidPort3, was issued.",
            string.Empty);

        Assert.NotNull(observation);
        Assert.Equal(3, observation!.RaidPortNumber);
    }

    [Fact]
    public void DiskHardwareFailureIsCriticalAndImmediate()
    {
        var observation = EventObservationParser.ParseEvent(
            "disk",
            154,
            2,
            50,
            DateTime.Now,
            "Windows reported a hardware failure.",
            EventXml(("value0", @"\Device\Harddisk6\DR9")));

        Assert.NotNull(observation);
        Assert.Equal(6, observation!.DiskNumber);
        Assert.Equal(ObservationSeverity.Critical, observation.Severity);
        Assert.Equal(AlertMode.Immediate, observation.AlertMode);
    }

    [Fact]
    public void RecordIdsFromDifferentChannelsHaveDifferentEvidenceKeys()
    {
        var system = EventObservationParser.ParseEvent(
            "Microsoft-Windows-Ntfs",
            140,
            3,
            51,
            DateTime.Now,
            "The transaction log could not be flushed.",
            string.Empty,
            channel: "System");
        var operational = EventObservationParser.ParseEvent(
            "Microsoft-Windows-Ntfs",
            140,
            3,
            51,
            DateTime.Now,
            "The transaction log could not be flushed.",
            string.Empty,
            channel: "Microsoft-Windows-Ntfs/Operational");

        Assert.NotNull(system);
        Assert.NotNull(operational);
        Assert.NotEqual(system!.EvidenceKey, operational!.EvidenceKey);
    }

    [Fact]
    public void OperationalStatusArrayIsMapped()
    {
        var statuses = StorageInventory.MapOperationalStatusCodes(new ushort[] { 2, 5, 13 });

        Assert.Equal(ExpectedOperationalStatuses, statuses);
    }

    [Fact]
    public void OnlineOperationalStatusIsMapped()
    {
        var statuses = StorageInventory.MapOperationalStatusCodes(new ushort[] { 0xD010 });

        Assert.Equal("Online", Assert.Single(statuses));
    }

    [Fact]
    public void DriveLetterIsNotTakenFromUnrelatedXmlText()
    {
        var observation = EventObservationParser.ParseEvent(
            "disk",
            153,
            3,
            48,
            DateTime.Now,
            "The disk I/O operation was retried.",
            EventXml(("Metadata", "The provider wrote C:\\temporary metadata.")));

        Assert.NotNull(observation);
        Assert.Null(observation!.DriveLetter);
    }

    [Fact]
    public void DriveLetterIsNotTakenFromNonNtfsDescription()
    {
        var observation = EventObservationParser.ParseEvent(
            "disk",
            153,
            3,
            52,
            DateTime.Now,
            @"A diagnostic was written to C:\temporary before the disk retry.",
            string.Empty);

        Assert.NotNull(observation);
        Assert.Null(observation!.DriveLetter);
    }

    private static string EventXml(params (string Name, string Value)[] values) =>
        $"<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><EventData>{string.Concat(values.Select(value => $"<Data Name=\"{value.Name}\">{System.Security.SecurityElement.Escape(value.Value)}</Data>"))}</EventData></Event>";
}
