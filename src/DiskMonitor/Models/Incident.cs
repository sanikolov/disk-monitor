namespace DiskMonitor.Models;

internal sealed class IncidentEvidence
{
    public DateTimeOffset Timestamp { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public int? EventId { get; init; }
    public long? EventRecordId { get; init; }
}

internal sealed class Incident
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Fingerprint { get; set; } = string.Empty;
    public string DeviceKey { get; set; } = "unknown";
    public string DeviceName { get; set; } = "Unknown storage device";
    public ObservationCategory Category { get; init; }
    public ObservationSeverity Severity { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public int EvidenceCount { get; set; }
    public bool Acknowledged { get; set; }
    public bool Resolved { get; set; }
    public DateTimeOffset? LastAlerted { get; set; }
    public List<IncidentEvidence> Evidence { get; set; } = new();

    public string Headline => $"{DeviceName}: {Category} issue";
}
