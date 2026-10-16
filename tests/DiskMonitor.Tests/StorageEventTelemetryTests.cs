using System.Text.Json;
using DiskMonitor.Infrastructure;
using DiskMonitor.Models;
using DiskMonitor.Monitoring;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class StorageEventTelemetryTests
{
    [Fact]
    public void EventReplayRotationPreservesIndependentProbeLogAndExposesWriteFailures()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DiskMonitor-telemetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var monitorPath = Path.Combine(directory, "monitor.log");
            var eventPath = Path.Combine(directory, "storage-events.log");
            new MonitorLogger(monitorPath).Info("Hardware probe started: preserved timing evidence");
            var originalProbeLog = File.ReadAllText(monitorPath);
            var telemetry = new StorageEventTelemetry(new MonitorLogger(eventPath));
            File.WriteAllText(eventPath, new string('x', 2 * 1024 * 1024));
            var observation = new StorageObservation { EventId = 507, EventRecordId = 1, Channel = "ClassPnP" };

            telemetry.Record(observation, null, null, null);

            Assert.True(File.Exists(eventPath + ".1"));
            Assert.Equal(originalProbeLog, File.ReadAllText(monitorPath));
            Assert.Null(telemetry.GetWriteError());
            var failedTelemetry = new StorageEventTelemetry(new MonitorLogger(directory));
            failedTelemetry.Record(observation, null, null, null);
            Assert.NotNull(failedTelemetry.GetWriteError());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EveryDistinctEventIsLoggedBeforeAlertThresholdAndReplayIsDeduplicatedPerRun()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DiskMonitor-telemetry-{Guid.NewGuid():N}.log");
        try
        {
            var logger = new MonitorLogger(path);
            var telemetry = new StorageEventTelemetry(logger);
            var time = DateTimeOffset.UtcNow;
            var observation = new StorageObservation
            {
                Timestamp = time,
                EventId = 507,
                EventRecordId = 135400,
                Channel = "ClassPnP",
                RuleId = "storage.command-failed",
                EventProcessId = 0,
                EventThreadId = 0,
                Details = "first line\nsecond line",
                RawEventXml = "<Event><test /></Event>"
            };
            var device = new DeviceSnapshot { DiskNumber = 6, SerialNumber = "test-serial", PnpDeviceId = "USBSTOR\\test", FirmwareVersion = "test-firmware" };

            telemetry.Record(observation, device, time.AddSeconds(1), "test inventory issue");
            telemetry.Record(observation, device, time.AddSeconds(1), null);

            var line = Assert.Single(File.ReadAllLines(path));
            using var json = JsonDocument.Parse(line[line.IndexOf('{', StringComparison.Ordinal)..]);
            Assert.Equal(135400, json.RootElement.GetProperty("EventRecordId").GetInt64());
            Assert.Equal("<Event><test /></Event>", json.RootElement.GetProperty("RawEventXml").GetString());
            Assert.Equal(observation.Details, json.RootElement.GetProperty("Details").GetString());
            Assert.Equal("test-serial", json.RootElement.GetProperty("Device").GetProperty("SerialNumber").GetString());
            Assert.True(json.RootElement.GetProperty("InventoryMayPostdateEvent").GetBoolean());
            Assert.Equal("test inventory issue", json.RootElement.GetProperty("InventoryIssue").GetString());
            Assert.Contains("does not establish", json.RootElement.GetProperty("ExecutionContextMeaning").GetString(), StringComparison.Ordinal);

            telemetry.Record(new StorageObservation
            {
                Timestamp = time, EventId = 500, EventRecordId = 135401, Channel = "ClassPnP",
                RuleId = "storage.read-failed", RawEventXml = "<Event />"
            }, null, null, null);
            Assert.Equal(2, File.ReadAllLines(path).Length);

            // A new run still captures this record even if incident state already
            // deduplicated it before the upgrade installed richer telemetry.
            new StorageEventTelemetry(logger).Record(observation, null, null, null);
            Assert.Equal(3, File.ReadAllLines(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IncidentEvidenceIncludesExactEventIdentityAndDeviceContext()
    {
        var observation = new StorageObservation
        {
            EventId = 507, EventRecordId = 42, Source = "StorDiag", Channel = "ClassPnP",
            EventProcessId = 0, EventThreadId = 0, Details = "Decoded command and sense"
        };

        var details = StorageEventTelemetry.DescribeEvidence(observation, new DeviceSnapshot
        {
            DiskNumber = 6, SerialNumber = "serial", PnpDeviceId = "USBSTOR\\test", BusType = "USB"
        });

        Assert.Contains("Decoded command and sense", details, StringComparison.Ordinal);
        Assert.Contains("record=42", details, StringComparison.Ordinal);
        Assert.Contains("channel=ClassPnP", details, StringComparison.Ordinal);
        Assert.Contains("PID=0", details, StringComparison.Ordinal);
        Assert.Contains("not proof", details, StringComparison.Ordinal);
        Assert.Contains("may postdate event", details, StringComparison.Ordinal);
        Assert.Contains("PNP=USBSTOR\\test", details, StringComparison.Ordinal);
    }
}
