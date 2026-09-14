using System.Management;
using DiskMonitor.Infrastructure;
using DiskMonitor.Models;

namespace DiskMonitor.Storage;

// A failed USB descriptor request never reaches MSFT_Disk or Win32_DiskDrive.
// Keep PnP evidence separate: an unknown USB endpoint is not a physical disk,
// and must never receive a guessed disk number, capacity, or SMART request.
internal sealed class PnpProblemInventory(MonitorLogger logger)
{
    internal const string ProblemRule = "pnp.current-problem";

    public IReadOnlyList<StorageObservation> Read()
    {
        var observations = new List<StorageObservation>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2",
                "SELECT Name, PNPDeviceID, PNPClass, Present, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0",
                new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(10) });
            using var results = searcher.Get();
            var now = DateTimeOffset.UtcNow;
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var observation = CreateObservation(
                        item["PNPDeviceID"]?.ToString() ?? string.Empty,
                        item["Name"]?.ToString() ?? string.Empty,
                        item["PNPClass"]?.ToString() ?? string.Empty,
                        item["Present"] is true,
                        Convert.ToUInt32(item["ConfigManagerErrorCode"], System.Globalization.CultureInfo.InvariantCulture),
                        now);
                    if (observation is not null)
                    {
                        observations.Add(observation);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"PnP problem inventory failed; process={Environment.ProcessId}; provider=Win32_PnPEntity; USB/disk device detection coverage is incomplete.", ex);
            // A failed query must not masquerade as an empty, healthy inventory.
            observations.Add(new StorageObservation
            {
                RuleId = "monitor.pnp-inventory-unavailable",
                Source = "Win32_PnPEntity",
                DeviceToken = "pnp-inventory",
                Category = ObservationCategory.Health,
                Severity = ObservationSeverity.Warning,
                AlertMode = AlertMode.Immediate,
                Summary = "USB and disk device problem checks are unavailable.",
                Details = $"Windows Plug and Play could not be queried; monitoring coverage is incomplete. {ex.GetType().Name}: {ex.Message}"
            });
        }

        return observations;
    }

    internal static StorageObservation? CreateObservation(
        string instanceId, string name, string deviceClass, bool present, uint problemCode, DateTimeOffset timestamp)
    {
        var usb = instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
                  instanceId.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase) ||
                  deviceClass.Equals("USB", StringComparison.OrdinalIgnoreCase);
        var storage = deviceClass.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase) ||
                      deviceClass.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase) ||
                      deviceClass.Equals("HDC", StringComparison.OrdinalIgnoreCase);
        // Windows retains disconnected devices indefinitely. Only present failed
        // nodes are evidence of a current fault; unrelated devices are out of scope.
        if (!present || problemCode == 0 || (!usb && !storage) || string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        var displayName = string.IsNullOrWhiteSpace(name) ? "Unidentified device" : name.Trim();
        return new StorageObservation
        {
            Timestamp = timestamp,
            Source = "Win32_PnPEntity",
            RuleId = ProblemRule,
            DeviceToken = instanceId,
            Category = usb ? ObservationCategory.Usb : ObservationCategory.Storage,
            Severity = ObservationSeverity.Error,
            AlertMode = AlertMode.Immediate,
            Summary = $"{displayName}: Windows device problem code {problemCode}.",
            Details = $"Observed={timestamp:O}; device={displayName}; instance={instanceId}; class={deviceClass}; present=True; ConfigManagerErrorCode={problemCode}. " +
                      (usb ? "USB communication or device setup has failed. An unidentified USB device cannot be confirmed as a disk; check its cable, power, and port."
                           : "Windows reports a storage device or controller problem; inspect its Device Manager status.")
        };
    }
}
