using DiskMonitor.Events;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class StorageCommandDiagnosticsTests
{
    [Theory]
    [InlineData("12010000FF00", "INQUIRY")]
    [InlineData("9E100000000000000000000000200000", "READ CAPACITY (16)")]
    [InlineData("FE0000000000", "unknown command")]
    [InlineData("", "malformed CDB")]
    [InlineData("00zz", "malformed CDB")]
    [InlineData("0", "malformed CDB")]
    public void CommandDecodingHandlesRealProbesAndMalformedPayloads(string cdb, string expected)
    {
        var result = StorageCommandDiagnostics.Describe("failure", new Dictionary<string, string> { ["CdbBytes"] = cdb });

        Assert.Contains(expected, result, StringComparison.Ordinal);
        Assert.Contains($"CdbBytes={cdb}", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("196", "0", "cause not reportable")]
    [InlineData("0xC4", "0x01", "becoming ready")]
    [InlineData("132", "2", "initializing command required")]
    [InlineData("132", "3", "manual intervention required")]
    [InlineData("132", "99", "not decoded")]
    [InlineData("132", null, "unavailable")]
    public void QualifierAndHexValuesDistinguishReadinessConditions(string srb, string? qualifier, string expected)
    {
        var fields = new Dictionary<string, string>
        {
            ["SrbStatus"] = srb, ["SenseKey"] = "0x02", ["AdditionalSenseCode"] = "4"
        };
        if (qualifier is not null)
        {
            fields["AdditionalSenseCodeQualifier"] = qualifier;
        }

        var result = StorageCommandDiagnostics.Describe("failure", fields);

        Assert.Contains(expected, result, StringComparison.Ordinal);
        if (qualifier is null)
        {
            Assert.DoesNotContain("cause not reportable", result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InvalidSenseIsNotPresentedAsAnEstablishedDeviceCondition()
    {
        var result = StorageCommandDiagnostics.Describe("failure", new Dictionary<string, string>
        {
            ["SrbStatus"] = "4", ["SenseKey"] = "2", ["AdditionalSenseCode"] = "4",
            ["AdditionalSenseCodeQualifier"] = "0", ["FutureField"] = "retained"
        });

        Assert.Contains("Sense data is unverified", result, StringComparison.Ordinal);
        Assert.DoesNotContain("cause not reportable", result, StringComparison.Ordinal);
        Assert.Contains("FutureField=retained", result, StringComparison.Ordinal);
    }
}
