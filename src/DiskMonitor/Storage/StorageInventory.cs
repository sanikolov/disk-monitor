using System.Management;
using System.Text.RegularExpressions;
using DiskMonitor.Infrastructure;
using DiskMonitor.Models;

namespace DiskMonitor.Storage;

internal sealed class StorageInventory
{
    private static readonly Regex DiskNumberRegex = new(@"Disk\s*#(?<number>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DriveLetterRegex = new(@"DeviceID=""(?<drive>[A-Za-z]:)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly MonitorLogger _logger;

    public StorageInventory(MonitorLogger logger)
    {
        _logger = logger;
    }

    public StorageInventorySnapshot Read()
    {
        var driveLetters = ReadDriveLetters();
        var legacyDevices = TryReadWin32Disks(driveLetters, out var legacyReadSucceeded);

        try
        {
            var devices = ReadMsftDisks(driveLetters);
            if (devices.Count > 0)
            {
                MergeTransportMetadata(devices, legacyDevices);
                return new StorageInventorySnapshot { Devices = devices, IsReliable = true };
            }

            if (legacyReadSucceeded)
            {
                return new StorageInventorySnapshot { Devices = legacyDevices, IsReliable = true };
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("The Windows storage-management provider could not be queried.", ex);
        }

        return new StorageInventorySnapshot { Devices = legacyDevices, IsReliable = legacyReadSucceeded };
    }

    private static List<DeviceSnapshot> ReadMsftDisks(Dictionary<int, IReadOnlyList<string>> driveLetters)
    {
        const string query = "SELECT Number, FriendlyName, SerialNumber, Manufacturer, Model, FirmwareVersion, HealthStatus, OperationalStatus, BusType, UniqueId, IsOffline, Size FROM MSFT_Disk";
        using var searcher = CreateSearcher(@"root\Microsoft\Windows\Storage", query);
        var devices = new List<DeviceSnapshot>();

        foreach (ManagementObject item in searcher.Get())
        {
            var diskNumber = GetInt32(item, "Number");
            var model = FirstNonEmpty(GetString(item, "FriendlyName"), GetString(item, "Model"), GetString(item, "Manufacturer"));
            var serial = GetString(item, "SerialNumber");
            var uniqueId = GetString(item, "UniqueId");
            var busType = BusTypeName(GetUInt16(item, "BusType"));

            devices.Add(new DeviceSnapshot
            {
                DiskNumber = diskNumber,
                Key = BuildKey(diskNumber, model, serial, uniqueId, busType, string.Empty),
                Model = model,
                SerialNumber = serial,
                FirmwareVersion = GetString(item, "FirmwareVersion"),
                BusType = busType,
                SizeBytes = GetUInt64(item, "Size"),
                HealthStatus = GetUInt16(item, "HealthStatus", 5),
                OperationalStatus = MapOperationalStatusCodes(GetUInt16Array(item, "OperationalStatus")),
                IsOffline = GetBoolean(item, "IsOffline"),
                DriveLetters = driveLetters.TryGetValue(diskNumber, out var letters) ? letters : Array.Empty<string>()
            });
        }

        return devices;
    }

    private List<DeviceSnapshot> TryReadWin32Disks(Dictionary<int, IReadOnlyList<string>> driveLetters, out bool succeeded)
    {
        try
        {
            var devices = ReadWin32Disks(driveLetters);
            succeeded = true;
            return devices;
        }
        catch (Exception ex)
        {
            _logger.Warn("The legacy disk inventory provider could not be queried.", ex);
            succeeded = false;
            return new List<DeviceSnapshot>();
        }
    }

    private static List<DeviceSnapshot> ReadWin32Disks(Dictionary<int, IReadOnlyList<string>> driveLetters)
    {
        const string query = "SELECT Index, Model, SerialNumber, PNPDeviceID, InterfaceType, Size, Status, SCSIPort, SCSITargetId, DeviceID FROM Win32_DiskDrive";
        using var searcher = CreateSearcher(@"root\cimv2", query);
        var devices = new List<DeviceSnapshot>();

        foreach (ManagementObject item in searcher.Get())
        {
            var diskNumber = GetInt32(item, "Index");
            var model = GetString(item, "Model");
            var serial = GetString(item, "SerialNumber");
            var pnpId = GetString(item, "PNPDeviceID");
            var busType = GetString(item, "InterfaceType");
            if (string.IsNullOrWhiteSpace(busType))
            {
                busType = "Unknown";
            }

            devices.Add(new DeviceSnapshot
            {
                DiskNumber = diskNumber,
                Key = BuildKey(diskNumber, model, serial, string.Empty, busType, pnpId),
                Model = model,
                SerialNumber = serial,
                PnpDeviceId = pnpId,
                ScsiPort = GetNullableInt32(item, "SCSIPort"),
                ScsiTargetId = GetNullableInt32(item, "SCSITargetId"),
                DevicePath = GetString(item, "DeviceID"),
                BusType = busType,
                SizeBytes = GetUInt64(item, "Size"),
                HealthStatus = 5,
                OperationalStatus = new[] { NormalizeWin32Status(GetString(item, "Status")) },
                DriveLetters = driveLetters.TryGetValue(diskNumber, out var letters) ? letters : Array.Empty<string>()
            });
        }

        return devices;
    }

    internal static void MergeTransportMetadata(List<DeviceSnapshot> devices, IReadOnlyList<DeviceSnapshot> legacyDevices)
    {
        var legacyByDisk = legacyDevices
            .Where(device => device.DiskNumber >= 0)
            .GroupBy(device => device.DiskNumber)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var device in devices)
        {
            if (!legacyByDisk.TryGetValue(device.DiskNumber, out var legacy))
            {
                continue;
            }

            device.ScsiPort ??= legacy.ScsiPort;
            device.ScsiTargetId ??= legacy.ScsiTargetId;
            if (string.IsNullOrWhiteSpace(device.DevicePath))
            {
                device.DevicePath = legacy.DevicePath;
            }

            if (string.IsNullOrWhiteSpace(device.PnpDeviceId))
            {
                device.PnpDeviceId = legacy.PnpDeviceId;
            }

            if (device.Key.Equals($"disk:{device.DiskNumber}", StringComparison.Ordinal))
            {
                device.Key = BuildKey(
                    device.DiskNumber,
                    device.Model,
                    device.SerialNumber,
                    string.Empty,
                    device.BusType,
                    device.PnpDeviceId);
            }
        }
    }

    private Dictionary<int, IReadOnlyList<string>> ReadDriveLetters()
    {
        const string query = "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition";
        var result = new Dictionary<int, List<string>>();

        try
        {
            using var searcher = CreateSearcher(@"root\cimv2", query);
            foreach (ManagementObject item in searcher.Get())
            {
                var antecedent = GetString(item, "Antecedent");
                var dependent = GetString(item, "Dependent");
                var diskMatch = DiskNumberRegex.Match(antecedent);
                var driveMatch = DriveLetterRegex.Match(dependent);
                if (!diskMatch.Success || !driveMatch.Success || !int.TryParse(diskMatch.Groups["number"].Value, out var diskNumber))
                {
                    continue;
                }

                var drive = driveMatch.Groups["drive"].Value.ToUpperInvariant();
                if (!result.TryGetValue(diskNumber, out var letters))
                {
                    letters = new List<string>();
                    result[diskNumber] = letters;
                }

                if (!letters.Contains(drive, StringComparer.OrdinalIgnoreCase))
                {
                    letters.Add(drive);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Volume-to-disk associations could not be queried.", ex);
        }

        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);
    }

    private static ManagementObjectSearcher CreateSearcher(string scopePath, string query)
    {
        var scope = new ManagementScope(scopePath);
        scope.Connect();
        var options = new System.Management.EnumerationOptions
        {
            ReturnImmediately = true,
            Rewindable = false,
            Timeout = TimeSpan.FromSeconds(10)
        };
        return new ManagementObjectSearcher(scope, new ObjectQuery(query), options);
    }

    internal static string BuildKey(int diskNumber, string model, string serial, string uniqueId, string busType, string pnpId)
    {
        var identity = !string.IsNullOrWhiteSpace(uniqueId)
            ? $"uid:{uniqueId}"
            : !string.IsNullOrWhiteSpace(serial)
                ? $"serial:{model}|{serial}|{busType}"
                : !string.IsNullOrWhiteSpace(pnpId)
                    ? $"pnp:{pnpId}"
                    : $"disk:{diskNumber}";

        return identity.Trim().ToLowerInvariant();
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string GetString(ManagementBaseObject item, string property) => item[property]?.ToString()?.Trim() ?? string.Empty;

    private static int GetInt32(ManagementBaseObject item, string property) =>
        item[property] is null ? -1 : Convert.ToInt32(item[property], System.Globalization.CultureInfo.InvariantCulture);

    private static int? GetNullableInt32(ManagementBaseObject item, string property) =>
        item[property] is null ? null : Convert.ToInt32(item[property], System.Globalization.CultureInfo.InvariantCulture);

    private static ushort GetUInt16(ManagementBaseObject item, string property, ushort defaultValue = 0) =>
        item[property] is null ? defaultValue : Convert.ToUInt16(item[property], System.Globalization.CultureInfo.InvariantCulture);

    private static ushort[] GetUInt16Array(ManagementBaseObject item, string property)
    {
        if (item[property] is not Array values)
        {
            return item[property] is null
                ? Array.Empty<ushort>()
                : new[] { Convert.ToUInt16(item[property], System.Globalization.CultureInfo.InvariantCulture) };
        }

        return values.Cast<object>()
            .Select(value => Convert.ToUInt16(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static ulong GetUInt64(ManagementBaseObject item, string property) =>
        item[property] is null ? 0 : Convert.ToUInt64(item[property], System.Globalization.CultureInfo.InvariantCulture);

    private static bool GetBoolean(ManagementBaseObject item, string property) =>
        item[property] is not null && Convert.ToBoolean(item[property], System.Globalization.CultureInfo.InvariantCulture);

    internal static string[] MapOperationalStatusCodes(IEnumerable<ushort> statuses)
    {
        var names = statuses.Select(OperationalStatusName).ToArray();
        return names.Length == 0 ? new[] { "Unknown" } : names;
    }

    private static string NormalizeWin32Status(string status) => status.Equals("Pred Fail", StringComparison.OrdinalIgnoreCase)
        ? "Predictive failure"
        : string.IsNullOrWhiteSpace(status) ? "Unknown" : status;

    private static string BusTypeName(ushort busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        _ => "Unknown"
    };

    private static string OperationalStatusName(ushort status) => status switch
    {
        2 => "OK",
        3 => "Degraded",
        4 => "Stressed",
        5 => "Predictive failure",
        6 => "Error",
        7 => "Non-recoverable error",
        10 => "Stopped",
        12 => "No contact",
        13 => "Lost communication",
        0xD010 => "Online",
        0xD011 => "Not ready",
        0xD012 => "No media",
        0xD013 => "Offline",
        0xD014 => "Failed",
        _ => status == 0 ? "Unknown" : $"Status {status}"
    };
}
