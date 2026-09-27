using System.Globalization;
using System.Text.Json;
using DiskMonitor.Infrastructure;
using DiskMonitor.Models;

namespace DiskMonitor.Storage;

internal sealed class HardwareInventoryProbeResult
{
    public StorageInventorySnapshot Inventory { get; init; } = new();
    public string? WorkerLogError { get; init; }
}

internal sealed class HardwarePnpProbeResult
{
    public IReadOnlyList<StorageObservation> PnpProblems { get; init; } = Array.Empty<StorageObservation>();
    public string? WorkerLogError { get; init; }
}

internal sealed class HardwarePredictionProbeResult
{
    public int DiskNumber { get; init; }
    public bool Supported { get; init; }
    public bool PredictsFailure { get; init; }
    public int ErrorCode { get; init; }
}

internal static class HardwareProbeWorker
{
    internal const string PnpArgument = "--probe-pnp";
    internal const string InventoryArgument = "--probe-inventory";
    internal const string PredictionArgument = "--probe-predict";

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !args[0].StartsWith("--probe-", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            switch (args)
            {
                case [PnpArgument]:
                    RunPnp();
                    break;
                case [InventoryArgument]:
                    RunInventory();
                    break;
                case [PredictionArgument, var diskText]
                    when int.TryParse(diskText, NumberStyles.None, CultureInfo.InvariantCulture, out var diskNumber):
                    RunPrediction(diskNumber);
                    break;
                default:
                    Console.Error.WriteLine("Invalid hardware probe worker arguments.");
                    exitCode = 2;
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }

        return true;
    }

    private static void RunPnp()
    {
        // The worker has its own log file; parent heartbeat writes must not contend
        // with a child that Windows may leave blocked in a storage provider.
        var logger = CreateWorkerLogger("pnp");
        var problems = new PnpProblemInventory(logger).Read();
        WriteResult(new HardwarePnpProbeResult
        {
            PnpProblems = problems,
            WorkerLogError = logger.GetHealth().LastWriteError
        });
    }

    private static void RunInventory()
    {
        var logger = CreateWorkerLogger("inventory");
        var inventory = new StorageInventory(logger).Read();
        var result = new HardwareInventoryProbeResult
        {
            Inventory = inventory,
            WorkerLogError = logger.GetHealth().LastWriteError
        };
        WriteResult(result);
    }

    private static MonitorLogger CreateWorkerLogger(string probe) =>
        // The PnP and inventory children run concurrently; distinct files avoid
        // cross-process append collisions because the logger lock is process-local.
        new(Path.Combine(AppPaths.DataDirectory, $"probe-{probe}.log"));

    private static void RunPrediction(int diskNumber)
    {
        var supported = NativeStorage.TryPredictFailure(diskNumber, out var predictsFailure, out var errorCode);
        WriteResult(new HardwarePredictionProbeResult
        {
            DiskNumber = diskNumber,
            Supported = supported,
            PredictsFailure = predictsFailure,
            ErrorCode = errorCode
        });
    }

    private static void WriteResult<T>(T result)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(result));
        Console.Out.Flush();
    }
}
