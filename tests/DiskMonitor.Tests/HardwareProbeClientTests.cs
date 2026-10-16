using System.Diagnostics;
using System.Text.Json;
using DiskMonitor.Infrastructure;
using DiskMonitor.Models;
using DiskMonitor.Storage;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class HardwareProbeClientTests
{
    [Fact]
    public async Task PublishedWorkerModeReturnsPredictionWithoutStartingTheTray()
    {
        using var client = new HardwareProbeClient();

        // A deliberately absent disk exercises the installed EXE's worker mode
        // without sending an IOCTL to a real drive.
        var result = await client.RunPredictionAsync(999999, TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.Equal(999999, result.DiskNumber);
        Assert.False(result.Supported);
        Assert.NotEqual(0, result.ErrorCode);
    }

    [Fact]
    public async Task TimedOutWorkerIsReapedBeforeTheNextProbeStarts()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var slowScript = Path.Combine(directory, "slow.ps1");
            var fastScript = Path.Combine(directory, "fast.ps1");
            File.WriteAllText(slowScript, "Start-Sleep -Seconds 30");
            File.WriteAllText(fastScript,
                "[Console]::Out.WriteLine('{\"DiskNumber\":42,\"Supported\":false,\"PredictsFailure\":false,\"ErrorCode\":2}')");
            var useSlowScript = true;
            var logPath = Path.Combine(directory, "probes.log");
            using var client = new HardwareProbeClient((_, _) => PowerShellScript(useSlowScript ? slowScript : fastScript), new MonitorLogger(logPath));

            await Assert.ThrowsAsync<TimeoutException>(() =>
                client.RunPredictionAsync(42, TimeSpan.FromMilliseconds(500), CancellationToken.None));

            useSlowScript = false;
            var recovered = await client.RunPredictionAsync(42, TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.Equal(42, recovered.DiskNumber);
            Assert.Equal(2, recovered.ErrorCode);
            var log = File.ReadAllText(logPath);
            Assert.Contains("Hardware probe started: command=--probe-predict; disk=42", log, StringComparison.Ordinal);
            Assert.Contains("workerProcess=", log, StringComparison.Ordinal);
            Assert.Contains("elapsedMs=", log, StringComparison.Ordinal);
            Assert.Contains("termination=confirmed", log, StringComparison.Ordinal);
            Assert.Contains("exitCode=0", log, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PnpAndInventoryTransportPreserveSeparateResults()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var inventoryPayloadFile = Path.Combine(directory, "inventory.json");
            var pnpPayloadFile = Path.Combine(directory, "pnp.json");
            var inventoryScriptFile = Path.Combine(directory, "inventory.ps1");
            var pnpScriptFile = Path.Combine(directory, "pnp.ps1");
            var expectedInventory = new HardwareInventoryProbeResult
            {
                Inventory = new StorageInventorySnapshot
                {
                    IsReliable = true,
                    Devices = new[] { new DeviceSnapshot { DiskNumber = 7, Model = "Test drive", BusType = "USB" } }
                }
            };
            var expectedPnp = new HardwarePnpProbeResult
            {
                PnpProblems = new[]
                {
                    new StorageObservation
                    {
                        RuleId = "pnp.current-problem",
                        DeviceToken = "USB\\VID_TEST",
                        Category = ObservationCategory.Usb,
                        Severity = ObservationSeverity.Error
                    }
                }
            };
            File.WriteAllText(inventoryPayloadFile, JsonSerializer.Serialize(expectedInventory));
            File.WriteAllText(pnpPayloadFile, JsonSerializer.Serialize(expectedPnp));
            WritePayloadScript(inventoryScriptFile, inventoryPayloadFile);
            WritePayloadScript(pnpScriptFile, pnpPayloadFile);
            using var client = new HardwareProbeClient((command, _) =>
                PowerShellScript(command == HardwareProbeWorker.PnpArgument ? pnpScriptFile : inventoryScriptFile));

            var pnp = await client.RunPnpAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            var inventory = await client.RunInventoryAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

            Assert.Equal(7, Assert.Single(inventory.Inventory.Devices).DiskNumber);
            Assert.Equal("Test drive", Assert.Single(inventory.Inventory.Devices).Model);
            Assert.Equal("USB\\VID_TEST", Assert.Single(pnp.PnpProblems).DeviceToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WritePayloadScript(string scriptPath, string payloadPath)
    {
        var escapedPayloadPath = payloadPath.Replace("'", "''", StringComparison.Ordinal);
        File.WriteAllText(scriptPath, $"[Console]::Out.Write([IO.File]::ReadAllText('{escapedPayloadPath}'))");
    }

    private static ProcessStartInfo PowerShellScript(string scriptPath)
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(executable);
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        return startInfo;
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DiskMonitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
