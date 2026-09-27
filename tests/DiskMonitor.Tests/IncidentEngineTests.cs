using DiskMonitor.Infrastructure;
using DiskMonitor.Models;
using DiskMonitor.Monitoring;
using DiskMonitor.Storage;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class IncidentEngineTests
{
    [Fact]
    public void RepeatedRuleAlertsAtThresholdAndDeduplicatesRecordIds()
    {
        var store = new InMemoryStateStore();
        var options = new MonitorOptions { RepeatedEventThreshold = 2 };
        using var engine = new IncidentEngine(store, CreateLogger(), options);
        var alertCount = 0;
        engine.AlertRaised += (_, _) => alertCount++;

        engine.Process(CreateObservation(1, DateTimeOffset.UtcNow), null);
        engine.Process(CreateObservation(1, DateTimeOffset.UtcNow.AddSeconds(1)), null);
        engine.Process(CreateObservation(2, DateTimeOffset.UtcNow.AddSeconds(2)), null);

        var incident = Assert.Single(engine.GetIncidents());
        Assert.Equal(1, alertCount);
        Assert.Equal(2, incident.EvidenceCount);
        Assert.Equal(2, incident.Evidence.Count);
    }

    [Fact]
    public void HistoricalEvidenceIsRecordedWithoutRaisingAnAlert()
    {
        var store = new InMemoryStateStore();
        using var engine = new IncidentEngine(store, CreateLogger(), new MonitorOptions());
        var alertRaised = false;
        engine.AlertRaised += (_, _) => alertRaised = true;

        engine.Process(CreateObservation(3, DateTimeOffset.UtcNow.AddHours(-1), isHistorical: true), null);

        Assert.False(alertRaised);
        Assert.Single(engine.GetIncidents());
    }

    [Fact]
    public void CriticalObservationAlertsImmediatelyEvenForARepeatedRule()
    {
        using var engine = new IncidentEngine(
            new InMemoryStateStore(),
            CreateLogger(),
            new MonitorOptions { RepeatedEventThreshold = 3 });
        var alertCount = 0;
        engine.AlertRaised += (_, _) => alertCount++;

        engine.Process(
            CreateObservation(
                30,
                DateTimeOffset.UtcNow,
                severity: ObservationSeverity.Critical),
            null);

        Assert.Equal(1, alertCount);
    }

    [Fact]
    public void SeverityEscalationBypassesTheAlertCooldown()
    {
        using var engine = new IncidentEngine(
            new InMemoryStateStore(),
            CreateLogger(),
            new MonitorOptions { AlertCooldownMinutes = 30 });
        var alertCount = 0;
        engine.AlertRaised += (_, _) => alertCount++;
        var now = DateTimeOffset.UtcNow;

        engine.Process(
            CreateObservation(31, now, severity: ObservationSeverity.Error, alertMode: AlertMode.Immediate),
            null);
        engine.Process(
            CreateObservation(32, now.AddMinutes(1), severity: ObservationSeverity.Critical, alertMode: AlertMode.Immediate),
            null);

        Assert.Equal(2, alertCount);
        Assert.Equal(ObservationSeverity.Critical, Assert.Single(engine.GetIncidents()).Severity);
    }

    [Fact]
    public void ReturnedIncidentsAreDeepCopies()
    {
        using var engine = new IncidentEngine(new InMemoryStateStore(), CreateLogger(), new MonitorOptions());
        engine.Process(CreateObservation(4, DateTimeOffset.UtcNow), null);

        var view = Assert.Single(engine.GetIncidents());
        view.Evidence.Clear();

        Assert.Single(Assert.Single(engine.GetIncidents()).Evidence);
    }

    [Fact]
    public void ProcessedEvidenceKeysSurviveRestart()
    {
        var store = new InMemoryStateStore();
        var observation = CreateObservation(5, DateTimeOffset.UtcNow);
        using (var firstEngine = new IncidentEngine(store, CreateLogger(), new MonitorOptions()))
        {
            firstEngine.Process(observation, null);
        }

        using var secondEngine = new IncidentEngine(store, CreateLogger(), new MonitorOptions());
        secondEngine.Process(observation, null);

        var incident = Assert.Single(secondEngine.GetIncidents());
        Assert.Equal(1, incident.EvidenceCount);
    }

    [Fact]
    public void CurrentBootIncidentIsNotResolvedJustBecauseItIsOver24HoursOld()
    {
        var store = new InMemoryStateStore();
        var now = DateTimeOffset.UtcNow;
        using var engine = new IncidentEngine(store, CreateLogger(), new MonitorOptions());
        engine.Process(CreateObservation(6, now.AddHours(-25), isHistorical: true), null);

        engine.ResolveBefore(now.AddDays(-7));

        Assert.False(Assert.Single(engine.GetIncidents()).Resolved);
    }

    [Fact]
    public void PreviousBootIncidentIsResolvedBeforeCurrentEvidenceIsProcessed()
    {
        var bootTime = DateTimeOffset.UtcNow.AddHours(-1);
        using var engine = new IncidentEngine(new InMemoryStateStore(), CreateLogger(), new MonitorOptions());
        engine.Process(CreateObservation(7, bootTime.AddMinutes(-1), isHistorical: true), null);

        engine.ResolveBefore(bootTime);
        engine.Process(CreateObservation(8, bootTime.AddMinutes(1)), null);

        var incidents = engine.GetIncidents();
        Assert.Equal(2, incidents.Count);
        Assert.Contains(incidents, incident => incident.Resolved && incident.LastSeen < bootTime);
        Assert.Contains(incidents, incident => !incident.Resolved && incident.FirstSeen >= bootTime);
    }

    [Fact]
    public void EvidenceHistoryIsCappedButTotalCountIsRetained()
    {
        using var engine = new IncidentEngine(new InMemoryStateStore(), CreateLogger(), new MonitorOptions());
        for (var id = 0; id < 25; id++)
        {
            engine.Process(CreateObservation(id + 10, DateTimeOffset.UtcNow.AddSeconds(id)), null);
        }

        var incident = Assert.Single(engine.GetIncidents());
        Assert.Equal(25, incident.EvidenceCount);
        Assert.Equal(20, incident.Evidence.Count);
    }

    [Fact]
    public void OutOfOrderEvidenceKeepsTheFullIncidentTimeRange()
    {
        var now = DateTimeOffset.UtcNow;
        using var engine = new IncidentEngine(new InMemoryStateStore(), CreateLogger(), new MonitorOptions());
        engine.Process(CreateObservation(40, now), null);
        engine.Process(CreateObservation(41, now.AddMinutes(-5)), null);

        var incident = Assert.Single(engine.GetIncidents());
        Assert.Equal(now.AddMinutes(-5), incident.FirstSeen);
        Assert.Equal(now, incident.LastSeen);
    }

    [Fact]
    public void FallbackIncidentIsReidentifiedAfterInventoryRefresh()
    {
        using var engine = new IncidentEngine(new InMemoryStateStore(), CreateLogger(), new MonitorOptions());
        engine.Process(CreateObservation(35, DateTimeOffset.UtcNow, diskNumber: 7), null);

        engine.ReidentifyDevice(new DeviceSnapshot
        {
            DiskNumber = 7,
            Key = "serial:usb-disk|123|usb",
            Model = "USB Disk",
            BusType = "USB"
        }, serialIsUnique: true);

        var incident = Assert.Single(engine.GetIncidents());
        Assert.Equal("serial:usb-disk|123|usb", incident.DeviceKey);
        Assert.Equal("USB Disk [USB]", incident.DeviceName);
    }

    [Fact]
    public void BuildKeyUsesStableIdentityPrecedence()
    {
        Assert.Equal("uid:unique", StorageInventory.BuildKey(1, "Model", "Serial", "Unique", "USB", "PNP"));
        Assert.Equal("serial:model|serial|usb", StorageInventory.BuildKey(1, "Model", "Serial", string.Empty, "USB", "PNP"));
        Assert.Equal("pnp:pnp", StorageInventory.BuildKey(1, "Model", string.Empty, string.Empty, "USB", "PNP"));
        Assert.Equal("disk:1", StorageInventory.BuildKey(1, string.Empty, string.Empty, string.Empty, "USB", string.Empty));
    }

    [Fact]
    public void LegacyPnpIdentityReplacesDiskNumberFallbackDuringMerge()
    {
        var modern = new DeviceSnapshot
        {
            DiskNumber = 4,
            Key = "disk:4",
            Model = "Generic USB Disk",
            BusType = "USB"
        };
        var legacy = new DeviceSnapshot
        {
            DiskNumber = 4,
            Key = "pnp:usbstor",
            PnpDeviceId = @"USBSTOR\DISK&VEN_GENERIC\123"
        };

        StorageInventory.MergeTransportMetadata(new List<DeviceSnapshot> { modern }, new[] { legacy });

        Assert.Equal(@"pnp:usbstor\disk&ven_generic\123", modern.Key);
        Assert.True(modern.IsUsb);
    }

    [Fact]
    public void LegacyUsbDeviceIsRecognizedFromItsPnpId()
    {
        var device = new DeviceSnapshot
        {
            BusType = "SCSI",
            PnpDeviceId = @"USBSTOR\DISK&VEN_GENERIC\123"
        };

        Assert.True(device.IsUsb);
    }

    [Fact]
    public void EmptyRemovableMediaIsNotTreatedAsAHealthFailure()
    {
        var device = new DeviceSnapshot { OperationalStatus = new[] { "No media" } };

        Assert.False(DiskMonitorCoordinator.HasAbnormalOperationalStatus(device));
    }

    private static StorageObservation CreateObservation(
        long eventRecordId,
        DateTimeOffset timestamp,
        bool isHistorical = false,
        int? diskNumber = null,
        ObservationSeverity severity = ObservationSeverity.Warning,
        AlertMode alertMode = AlertMode.Repeated) =>
        new()
        {
            Timestamp = timestamp,
            Source = "disk",
            EventRecordId = eventRecordId,
            RuleId = "disk.io-retry",
            Category = ObservationCategory.Storage,
            Severity = severity,
            AlertMode = alertMode,
            Summary = "test",
            Details = "test",
            DiskNumber = diskNumber,
            IsHistorical = isHistorical
        };

    private static MonitorLogger CreateLogger() => new(Path.Combine(AppContext.BaseDirectory, "test-monitor.log"));

    private sealed class InMemoryStateStore : IMonitorStateStore
    {
        public MonitorState State { get; private set; } = new();

        public MonitorState Load() => State;

        public void Save(MonitorState state) => State = state;
    }
}
