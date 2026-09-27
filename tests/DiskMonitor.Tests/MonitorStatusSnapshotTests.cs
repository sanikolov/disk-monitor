using DiskMonitor.Models;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class MonitorStatusSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] MissingSystemChannel = { "System" };

    [Fact]
    public void FirstInventoryGetsAStartupGracePeriodThenBecomesStale()
    {
        var status = new MonitorStatusSnapshot
        {
            StartedUtc = Now,
            InventoryTimeout = TimeSpan.FromSeconds(30),
            MaximumInventoryAge = TimeSpan.FromMinutes(31)
        };

        Assert.False(status.InventoryIsStale(Now.AddSeconds(30)));
        Assert.False(status.IsDegraded(Now.AddSeconds(30)));
        Assert.True(status.InventoryIsStale(Now.AddSeconds(31)));
        Assert.True(status.IsDegraded(Now.AddSeconds(31)));
    }

    [Fact]
    public void LastSuccessfulInventoryDeterminesFreshnessEvenAfterAnotherAttempt()
    {
        var status = new MonitorStatusSnapshot
        {
            StartedUtc = Now.AddHours(-2),
            LastInventorySuccessUtc = Now.AddMinutes(-31),
            LastInventoryAttemptUtc = Now,
            InventoryInProgress = true,
            InventoryTimeout = TimeSpan.FromSeconds(30),
            MaximumInventoryAge = TimeSpan.FromMinutes(30)
        };

        // A new attempt must not make the previously captured inventory look fresh.
        Assert.True(status.InventoryIsStale(Now));
        Assert.True(status.IsDegraded(Now));
    }

    [Fact]
    public void RecentSuccessIsFreshThroughMaximumAge()
    {
        var status = FreshStatus() with
        {
            LastInventorySuccessUtc = Now.AddMinutes(-30)
        };

        Assert.False(status.InventoryIsStale(Now));
        Assert.False(status.IsDegraded(Now));
        Assert.True(status.InventoryIsStale(Now.AddTicks(1)));
    }

    [Fact]
    public void OtherCoverageFailuresDegradeAnOtherwiseFreshMonitor()
    {
        var fresh = FreshStatus();

        Assert.True((fresh with { InventoryIssue = "Inventory query failed" }).IsDegraded(Now));
        Assert.True((fresh with { PnpIssue = "PnP query failed" }).IsDegraded(Now));
        Assert.True((fresh with { MissingEventChannels = MissingSystemChannel }).IsDegraded(Now));
        Assert.True((fresh with { DiagnosticLogError = "UnauthorizedAccessException" }).IsDegraded(Now));
    }

    [Fact]
    public void PendingEventSubscriptionsGetABriefStartupGrace()
    {
        var status = FreshStatus() with
        {
            StartedUtc = Now,
            MissingEventChannels = MissingSystemChannel,
            EventChannelStartupGrace = TimeSpan.FromSeconds(30)
        };

        Assert.True(status.EventChannelsAreStarting(Now.AddSeconds(29)));
        Assert.False(status.IsDegraded(Now.AddSeconds(29)));
        Assert.False(status.EventChannelsAreStarting(Now.AddSeconds(30)));
        Assert.True(status.IsDegraded(Now.AddSeconds(30)));
    }

    [Fact]
    public void MissingLoggerHeartbeatDegradesMonitoringEvenWithoutAnException()
    {
        var status = FreshStatus() with
        {
            LastDiagnosticWriteUtc = Now.AddMinutes(-6),
            MaximumDiagnosticLogAge = TimeSpan.FromMinutes(6)
        };

        Assert.False(status.DiagnosticLogIsStale(Now));
        Assert.True(status.DiagnosticLogIsStale(Now.AddTicks(1)));
        Assert.True(status.IsDegraded(Now.AddTicks(1)));
    }

    private static MonitorStatusSnapshot FreshStatus() => new()
    {
        StartedUtc = Now.AddHours(-2),
        LastInventorySuccessUtc = Now.AddMinutes(-1),
        InventoryTimeout = TimeSpan.FromSeconds(30),
        MaximumInventoryAge = TimeSpan.FromMinutes(30)
    };
}
