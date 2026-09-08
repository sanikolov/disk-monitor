using DiskMonitor.Models;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class RiskAssessmentTests
{
    [Fact]
    public void NoActiveIncidentsIsBenign()
    {
        var incidents = new[]
        {
            new Incident { Severity = ObservationSeverity.Critical, Resolved = true }
        };

        Assert.Equal(RiskLevel.Benign, RiskAssessment.Overall(incidents));
    }

    [Fact]
    public void NonCriticalActiveIncidentsAreSurvivable()
    {
        foreach (var severity in new[] { ObservationSeverity.Warning, ObservationSeverity.Error })
        {
            var incident = new Incident { Severity = severity };

            Assert.Equal(RiskLevel.Survivable, RiskAssessment.ForIncident(incident));
            Assert.Equal(RiskLevel.Survivable, RiskAssessment.Overall(new[] { incident }));
        }
    }

    [Fact]
    public void AnyActiveCriticalIncidentMakesOverallRiskCritical()
    {
        var incidents = new[]
        {
            new Incident { Severity = ObservationSeverity.Warning },
            new Incident { Severity = ObservationSeverity.Critical, Acknowledged = true }
        };

        Assert.Equal(RiskLevel.Critical, RiskAssessment.Overall(incidents));
    }

    [Fact]
    public void PreBootCriticalIncidentDoesNotAffectCurrentAssessment()
    {
        var bootTime = DateTimeOffset.UtcNow.AddHours(-1);
        var incidents = new[]
        {
            new Incident
            {
                Severity = ObservationSeverity.Critical,
                LastSeen = bootTime.AddMinutes(-1)
            }
        };

        Assert.Equal(RiskLevel.Benign, RiskAssessment.Overall(incidents, bootTime));
    }
}
