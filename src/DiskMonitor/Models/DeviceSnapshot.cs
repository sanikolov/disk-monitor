namespace DiskMonitor.Models;

internal sealed class DeviceSnapshot
{
    public int DiskNumber { get; init; }
    public string Key { get; set; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string FirmwareVersion { get; init; } = string.Empty;
    public string PnpDeviceId { get; set; } = string.Empty;
    public int? ScsiPort { get; set; }
    public int? ScsiTargetId { get; set; }
    public string DevicePath { get; set; } = string.Empty;
    public string BusType { get; init; } = "Unknown";
    public ulong SizeBytes { get; init; }
    public ushort HealthStatus { get; init; } = 5;
    public string[] OperationalStatus { get; init; } = Array.Empty<string>();
    public bool IsOffline { get; init; }
    public IReadOnlyList<string> DriveLetters { get; init; } = Array.Empty<string>();

    public bool IsUsb =>
        BusType.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
        PnpDeviceId.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase);

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Model) ? $"PhysicalDrive{DiskNumber}" : Model.Trim();
            var letters = DriveLetters.Count == 0 ? string.Empty : $" ({string.Join(", ", DriveLetters)})";
            return $"{name} [{BusType}]{letters}";
        }
    }
}

internal sealed class StorageInventorySnapshot
{
    public IReadOnlyList<DeviceSnapshot> Devices { get; init; } = Array.Empty<DeviceSnapshot>();
    public bool IsReliable { get; init; } = true;

    public DeviceSnapshot? FindByDiskNumber(int diskNumber) =>
        Devices.FirstOrDefault(device => device.DiskNumber == diskNumber);

    public DeviceSnapshot? FindByDriveLetter(string driveLetter) =>
        Devices.FirstOrDefault(device => device.DriveLetters.Contains(driveLetter, StringComparer.OrdinalIgnoreCase));

    public DeviceSnapshot? FindUniqueBySerialNumber(string? serialNumber)
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            return null;
        }

        var matches = Devices
            .Where(device => device.SerialNumber.Equals(serialNumber, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    // Storport names its adapter path RaidPortN; Win32_DiskDrive exposes the same
    // adapter as SCSIPort=N. This is the best standard-user correlation available.
    public DeviceSnapshot? FindByRaidPort(int raidPort)
    {
        var matches = Devices.Where(device => device.ScsiPort == raidPort).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
