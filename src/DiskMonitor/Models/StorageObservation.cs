namespace DiskMonitor.Models;

internal enum ObservationCategory
{
    Storage,
    Usb,
    Ntfs,
    Health
}

internal enum ObservationSeverity
{
    Warning = 2,
    Error = 3,
    Critical = 4
}

internal enum AlertMode
{
    Repeated,
    Immediate
}

internal sealed class StorageObservation : EventArgs
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Source { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public int? EventId { get; init; }
    public long? EventRecordId { get; init; }
    public string RuleId { get; init; } = string.Empty;
    public ObservationCategory Category { get; init; }
    public ObservationSeverity Severity { get; init; }
    public AlertMode AlertMode { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public int? DiskNumber { get; init; }
    public string? DeviceSerialNumber { get; init; }
    public int? RaidPortNumber { get; init; }
    public string? DriveLetter { get; init; }
    public string? DeviceToken { get; init; }
    public bool IsHistorical { get; init; }

    public string EvidenceKey =>
        EventRecordId is null
            ? $"{RuleId}|{Timestamp.UtcDateTime.Ticks}|{DiskNumber}|{DriveLetter}|{DeviceToken}"
            : string.IsNullOrWhiteSpace(Channel)
                ? $"{Source}|{EventRecordId.Value}"
                : $"{Channel.ToLowerInvariant()}|{EventRecordId.Value}";
}
