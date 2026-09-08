namespace DiskMonitor.Models;

internal sealed class MonitorState
{
    public List<Incident> Incidents { get; set; } = new();
    public List<string> ProcessedEvidenceKeys { get; set; } = new();
}
