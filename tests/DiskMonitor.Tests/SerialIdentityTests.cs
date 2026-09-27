using DiskMonitor.Infrastructure;
using DiskMonitor.Models;
using DiskMonitor.Monitoring;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class SerialIdentityTests
{
    [Fact]
    public void UnresolvedStorDiagEventsWithReusedSerialKeepSeparateDiskIdentities()
    {
        using var engine = CreateEngine();
        engine.Process(Observation(100, diskNumber: 5), null);
        engine.Process(Observation(101, diskNumber: 6), null);

        var incidents = engine.GetIncidents();
        Assert.Equal(2, incidents.Count);
        Assert.Contains(incidents, incident => incident.DeviceKey == "serial:shared-bridge-serial|disk:5");
        Assert.Contains(incidents, incident => incident.DeviceKey == "serial:shared-bridge-serial|disk:6");
    }

    [Fact]
    public void UnresolvedNtfsEventsWithReusedSerialKeepSeparateVolumeIdentities()
    {
        using var engine = CreateEngine();
        engine.Process(Observation(102, driveLetter: "D:"), null);
        engine.Process(Observation(103, driveLetter: "E:"), null);

        var incidents = engine.GetIncidents();
        Assert.Equal(2, incidents.Count);
        Assert.Contains(incidents, incident => incident.DeviceKey == "serial:shared-bridge-serial|volume:d:");
        Assert.Contains(incidents, incident => incident.DeviceKey == "serial:shared-bridge-serial|volume:e:");
    }

    [Fact]
    public void DistinctDisksWithReusedSerialAreReidentifiedByDiskNumber()
    {
        using var engine = CreateEngine();
        engine.Process(Observation(104, diskNumber: 5), null);
        engine.Process(Observation(105, diskNumber: 6), null);
        var first = Device(5, "uid:first-disk", "H:");
        var second = Device(6, "uid:second-disk", "J:");

        // Serial-only matching is ambiguous here; disk numbers separate these
        // current-boot observations until an inventory snapshot arrives.
        engine.ReidentifyDevice(first, serialIsUnique: false);
        engine.ReidentifyDevice(second, serialIsUnique: false);

        var incidents = engine.GetIncidents();
        Assert.Equal(2, incidents.Count);
        Assert.Contains(incidents, incident => incident.DeviceKey == first.Key && incident.DeviceName.Contains("H:", StringComparison.Ordinal));
        Assert.Contains(incidents, incident => incident.DeviceKey == second.Key && incident.DeviceName.Contains("J:", StringComparison.Ordinal));
    }

    [Fact]
    public void UniqueSerialFallbackIsReidentifiedWhenDiskNumberChanged()
    {
        using var engine = CreateEngine();
        engine.Process(Observation(106, diskNumber: 5), null);
        var device = Device(7, "uid:moved-disk", "H:");

        engine.ReidentifyDevice(device, serialIsUnique: true);

        var incident = Assert.Single(engine.GetIncidents());
        Assert.Equal(device.Key, incident.DeviceKey);
        Assert.Contains("H:", incident.DeviceName, StringComparison.Ordinal);
    }

    [Fact]
    public void UniqueSerialVolumeFallbackIsReidentifiedAfterInventoryArrives()
    {
        using var engine = CreateEngine();
        engine.Process(Observation(107, driveLetter: "D:"), null);
        var device = Device(1, "uid:sata-disk", "D:");

        engine.ReidentifyDevice(device, serialIsUnique: true);

        var incident = Assert.Single(engine.GetIncidents());
        Assert.Equal(device.Key, incident.DeviceKey);
        Assert.Contains("D:", incident.DeviceName, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryRejectsAReusedSerialAsAnAmbiguousMatch()
    {
        var first = Device(5, "uid:first-disk", "H:");
        var second = Device(6, "uid:second-disk", "J:");
        var inventory = new StorageInventorySnapshot { Devices = new[] { first, second } };

        Assert.Null(inventory.FindUniqueBySerialNumber("shared-bridge-serial"));
        Assert.Same(first, new StorageInventorySnapshot { Devices = new[] { first } }
            .FindUniqueBySerialNumber("SHARED-BRIDGE-SERIAL"));
    }

    private static IncidentEngine CreateEngine() =>
        new(new InMemoryStateStore(),
            new MonitorLogger(Path.Combine(AppContext.BaseDirectory, "serial-identity-test.log")),
            new MonitorOptions());

    private static DeviceSnapshot Device(int diskNumber, string key, string driveLetter) => new()
    {
        DiskNumber = diskNumber,
        Key = key,
        Model = "External disk",
        SerialNumber = "shared-bridge-serial",
        BusType = "USB",
        DriveLetters = new[] { driveLetter }
    };

    private static StorageObservation Observation(long recordId, int? diskNumber = null, string? driveLetter = null) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Source = "Microsoft-Windows-StorDiag",
        Channel = "Microsoft-Windows-Storage-ClassPnP/Operational",
        EventId = 507,
        EventRecordId = recordId,
        RuleId = "storage.command-failed",
        Category = ObservationCategory.Storage,
        Severity = ObservationSeverity.Warning,
        AlertMode = AlertMode.Repeated,
        Summary = "Failed non-read/write storage command.",
        Details = "Test event with a serial shared by two enclosures.",
        DeviceSerialNumber = "shared-bridge-serial",
        DiskNumber = diskNumber,
        DriveLetter = driveLetter
    };

    private sealed class InMemoryStateStore : IMonitorStateStore
    {
        private MonitorState _state = new();

        public MonitorState Load() => _state;

        public void Save(MonitorState state) => _state = state;
    }
}
