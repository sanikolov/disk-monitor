using DiskMonitor.Infrastructure;
using DiskMonitor.Models;
using DiskMonitor.Monitoring;
using DiskMonitor.Storage;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class PnpProblemInventoryTests
{
    private const string FailedUsb = @"USB\VID_0000&PID_0002\5&323A54A6&0&12";

    [Fact]
    public void DescriptorFailureRaisesRiskWithoutAnyEnumeratedDisk()
    {
        using var engine = CreateEngine();
        var alerts = 0;
        engine.AlertRaised += (_, _) => alerts++;
        var observation = Problem(FailedUsb, DateTimeOffset.UtcNow);

        engine.Process(observation, null);

        var incident = Assert.Single(engine.GetIncidents());
        Assert.Equal(1, alerts);
        Assert.Equal(RiskLevel.Survivable, RiskAssessment.Overall(engine.GetIncidents()));
        Assert.Null(observation.DiskNumber);
        Assert.Contains(FailedUsb, observation.Details);
        Assert.Contains("ConfigManagerErrorCode=43", observation.Details);
        Assert.Contains("Device Descriptor Request Failed", observation.Summary);
        Assert.Equal(ObservationCategory.Usb, incident.Category);
    }

    [Theory]
    [InlineData(false, 43, "USB", FailedUsb)]
    [InlineData(true, 0, "USB", FailedUsb)]
    [InlineData(true, 22, "Net", "PCI\\WIFI")]
    [InlineData(true, 43, "USB", "")]
    public void AbsentHealthyUnrelatedOrUnidentifiableDevicesAreNotReported(bool present, uint code, string deviceClass, string id)
    {
        Assert.Null(PnpProblemInventory.CreateObservation(id, "device", deviceClass, present, code, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("DiskDrive")]
    [InlineData("SCSIAdapter")]
    [InlineData("HDC")]
    public void FailedStorageDevicesAndControllersAreReported(string deviceClass)
    {
        var observation = PnpProblemInventory.CreateObservation("PCI\\STORAGE", "controller", deviceClass, true, 10, DateTimeOffset.UtcNow);
        Assert.NotNull(observation);
        Assert.Equal(ObservationCategory.Storage, observation.Category);
    }

    [Fact]
    public void SimultaneousUnidentifiedDevicesRemainSeparateIncidents()
    {
        using var engine = CreateEngine();
        var now = DateTimeOffset.UtcNow;
        engine.Process(Problem(FailedUsb, now), null);
        engine.Process(Problem(FailedUsb + "-other-port", now), null);

        Assert.Equal(2, engine.GetIncidents().Count);
    }

    [Fact]
    public void PersistentFaultSurvivesExpiryAndReopensAfterManualClearWithoutPollAlertSpam()
    {
        using var engine = CreateEngine();
        var alerts = 0;
        engine.AlertRaised += (_, _) => alerts++;
        var now = DateTimeOffset.UtcNow;
        engine.Process(Problem(FailedUsb, now), null);
        engine.Process(Problem(FailedUsb, now.AddSeconds(30)), null);
        Assert.Equal(1, alerts);
        Assert.Equal(2, Assert.Single(engine.GetIncidents()).EvidenceCount);

        engine.Process(Problem(FailedUsb, now.AddDays(2)), null);
        engine.AutoResolveStale(now.AddDays(2));
        var active = Assert.Single(engine.GetIncidents(activeOnly: true));
        engine.Resolve(active.Id);
        engine.Process(Problem(FailedUsb, now.AddDays(2).AddMinutes(1)), null);
        Assert.Single(engine.GetIncidents(activeOnly: true));
    }

    private static StorageObservation Problem(string id, DateTimeOffset now) =>
        PnpProblemInventory.CreateObservation(id, "Unknown USB Device (Device Descriptor Request Failed)", "USB", true, 43, now)!;

    private static IncidentEngine CreateEngine() => new(
        new MemoryStore(),
        new MonitorLogger(Path.Combine(Path.GetTempPath(), "DiskMonitor.Tests", "pnp-problems.log")),
        new MonitorOptions());

    private sealed class MemoryStore : IMonitorStateStore
    {
        public MonitorState Load() => new();
        public void Save(MonitorState state) { }
    }
}
