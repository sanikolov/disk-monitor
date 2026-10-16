using System.Text.Json;
using DiskMonitor.Infrastructure;
using DiskMonitor.Models;

namespace DiskMonitor.Monitoring;

// Called by the coordinator's single observation reader. This per-run cache is
// deliberately independent of persistent alert deduplication: upgrading/restarting
// must still collect richer diagnostics for retained records from this boot.
internal sealed class StorageEventTelemetry(MonitorLogger logger)
{
    private const int MaximumRecentKeys = 4096;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public string? GetWriteError() => logger.GetHealth().LastWriteError;

    public void Record(StorageObservation observation, DeviceSnapshot? device, DateTimeOffset? inventorySampleUtc, string? inventoryIssue)
    {
        if (observation.EventId is null || !_seen.Add(observation.EvidenceKey))
        {
            return;
        }

        _order.Enqueue(observation.EvidenceKey);
        while (_order.Count > MaximumRecentKeys)
        {
            _seen.Remove(_order.Dequeue());
        }

        // One JSON object per line keeps multiline descriptions/XML unambiguous.
        // MonitorLogger bounds disk use and reports write failures to the status UI.
        logger.Info($"Storage event telemetry: {JsonSerializer.Serialize(new
        {
            EventUtc = observation.Timestamp,
            observation.ReceivedUtc,
            LoggedUtc = DateTimeOffset.UtcNow,
            observation.Source,
            observation.Channel,
            observation.EventId,
            observation.EventRecordId,
            observation.EventProcessId,
            observation.EventThreadId,
            observation.EventActivityId,
            ExecutionContextMeaning = "Event publisher context; does not establish the initiating application. PID may be zero or reused.",
            observation.RuleId,
            observation.DiskNumber,
            observation.DeviceSerialNumber,
            observation.Details,
            InventorySampleUtc = inventorySampleUtc,
            InventoryMayPostdateEvent = inventorySampleUtc > observation.Timestamp,
            InventoryIssue = inventoryIssue,
            Device = device,
            observation.RawEventXml,
            observation.RawEventXmlTruncated
        })}");
    }

    public static string DescribeEvidence(StorageObservation observation, DeviceSnapshot? device)
    {
        if (observation.EventId is null)
        {
            return observation.Details;
        }

        var context = $"Event: {observation.Source}; ID={observation.EventId}; channel={observation.Channel}; record={observation.EventRecordId}; UTC={observation.Timestamp:O}.";
        if (observation.EventProcessId is not null || observation.EventThreadId is not null)
        {
            context += $"{Environment.NewLine}Event execution context: PID={observation.EventProcessId}; TID={observation.EventThreadId}; activity={observation.EventActivityId ?? "unavailable"}. This is publisher context, not proof of the initiating application.";
        }

        if (device is not null)
        {
            context += $"{Environment.NewLine}Latest known device (may postdate event): {device.DisplayName}; disk={device.DiskNumber}; serial={device.SerialNumber}; firmware={device.FirmwareVersion}; PNP={device.PnpDeviceId}; SCSI port={device.ScsiPort}; target={device.ScsiTargetId}.";
        }

        return $"{observation.Details}{Environment.NewLine}{context}";
    }
}
