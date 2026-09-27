namespace DiskMonitor.Models;

internal sealed record MonitorStatusSnapshot
{
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? LastInventoryAttemptUtc { get; init; }
    public DateTimeOffset? LastInventorySuccessUtc { get; init; }
    public bool InventoryInProgress { get; init; }
    public string? InventoryIssue { get; init; }
    public string? PnpIssue { get; init; }
    public TimeSpan MaximumInventoryAge { get; init; }
    public TimeSpan InventoryTimeout { get; init; }
    public IReadOnlyList<string> MissingEventChannels { get; init; } = Array.Empty<string>();
    public TimeSpan EventChannelStartupGrace { get; init; }
    public DateTimeOffset? LastDiagnosticWriteUtc { get; init; }
    public TimeSpan? MaximumDiagnosticLogAge { get; init; }
    public string? DiagnosticLogError { get; init; }
    public string DiagnosticLogPath { get; init; } = string.Empty;

    public bool InventoryIsStale(DateTimeOffset now) =>
        LastInventorySuccessUtc is null
            ? now - StartedUtc > InventoryTimeout
            : now - LastInventorySuccessUtc.Value > MaximumInventoryAge;

    public bool DiagnosticLogIsStale(DateTimeOffset now) =>
        MaximumDiagnosticLogAge is { } maximumAge &&
        (LastDiagnosticWriteUtc is null || now - LastDiagnosticWriteUtc.Value > maximumAge);

    public bool EventChannelsAreStarting(DateTimeOffset now) =>
        MissingEventChannels.Count > 0 && now - StartedUtc < EventChannelStartupGrace;

    public bool IsDegraded(DateTimeOffset now) =>
        InventoryIsStale(now) ||
        DiagnosticLogIsStale(now) ||
        !string.IsNullOrWhiteSpace(InventoryIssue) ||
        !string.IsNullOrWhiteSpace(PnpIssue) ||
        (MissingEventChannels.Count > 0 && !EventChannelsAreStarting(now)) ||
        !string.IsNullOrWhiteSpace(DiagnosticLogError);
}
