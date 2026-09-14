namespace DiskMonitor.Models;

internal enum RiskLevel
{
    Benign,
    Survivable,
    Critical
}

internal static class RiskAssessment
{
    public static RiskLevel Overall(IEnumerable<Incident> incidents, DateTimeOffset? occurredSince = null)
    {
        var active = incidents
            .Where(incident => !incident.Resolved &&
                               (occurredSince is null || incident.LastSeen >= occurredSince.Value))
            .ToList();
        if (active.Count == 0)
        {
            return RiskLevel.Benign;
        }

        return active.Any(incident => ForIncident(incident) == RiskLevel.Critical)
            ? RiskLevel.Critical
            : RiskLevel.Survivable;
    }

    public static RiskLevel ForIncident(Incident incident) =>
        incident.Severity == ObservationSeverity.Critical
            ? RiskLevel.Critical
            : RiskLevel.Survivable;

    public static string Label(RiskLevel risk) => risk switch
    {
        RiskLevel.Benign => "BENIGN",
        RiskLevel.Survivable => "SURVIVABLE",
        RiskLevel.Critical => "CRITICAL",
        _ => throw new ArgumentOutOfRangeException(nameof(risk), risk, null)
    };

    public static string Guidance(RiskLevel risk) => risk switch
    {
        RiskLevel.Benign => "No active Windows-reported storage issue since this reboot. Keep normal backups.",
        RiskLevel.Survivable => "USB, device detection, I/O, or monitoring trouble was detected. Protect accessible data and investigate; recovery is not assured.",
        RiskLevel.Critical => "Corruption or hardware failure was reported. Data loss may be imminent or may already have occurred; stop writes and protect the data now.",
        _ => throw new ArgumentOutOfRangeException(nameof(risk), risk, null)
    };
}
