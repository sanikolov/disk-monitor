using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DiskMonitor.Storage;

// Only the child touches WMI and physical-drive IOCTLs. If a provider ignores
// cancellation, the tray and event-log watchers remain in the parent process.
internal sealed class HardwareProbeClient : IDisposable
{
    private const int MaximumOutputCharacters = 2 * 1024 * 1024;
    private const int MaximumErrorCharacters = 64 * 1024;
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private readonly Func<string, int?, ProcessStartInfo> _startInfoFactory;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _processGate = new();
    private Process? _activeProcess;
    private bool _disposed;

    public HardwareProbeClient() : this(CreateStartInfo)
    {
    }

    internal HardwareProbeClient(Func<string, int?, ProcessStartInfo> startInfoFactory)
    {
        _startInfoFactory = startInfoFactory;
    }

    public Task<HardwarePnpProbeResult> RunPnpAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        RunWorkerAsync<HardwarePnpProbeResult>(HardwareProbeWorker.PnpArgument, null, timeout, cancellationToken);

    public Task<HardwareInventoryProbeResult> RunInventoryAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        RunWorkerAsync<HardwareInventoryProbeResult>(HardwareProbeWorker.InventoryArgument, null, timeout, cancellationToken);

    public Task<HardwarePredictionProbeResult> RunPredictionAsync(int diskNumber, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(diskNumber);
        return RunWorkerAsync<HardwarePredictionProbeResult>(HardwareProbeWorker.PredictionArgument, diskNumber, timeout, cancellationToken);
    }

    public void Dispose()
    {
        Process? active;
        lock (_processGate)
        {
            _disposed = true;
            active = _activeProcess;
        }

        if (active is not null)
        {
            try
            {
                if (!active.HasExited)
                {
                    active.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // The worker exited while shutdown was requesting termination.
            }
            catch (Win32Exception)
            {
                // The in-flight call will retain the process and report the failure.
            }
        }
    }

    private async Task<T> RunWorkerAsync<T>(
        string command,
        int? diskNumber,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var previousStopError = await StopActiveProcessAsync(killIfRunning: true).ConfigureAwait(false);
            if (previousStopError is not null)
            {
                throw new InvalidOperationException($"A previous hardware probe worker is still running: {previousStopError}");
            }

            var startInfo = _startInfoFactory(command, diskNumber);
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;
            var process = new Process { StartInfo = startInfo };
            try
            {
                lock (_processGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!process.Start())
                    {
                        throw new InvalidOperationException($"The hardware probe worker did not start: command={command}.");
                    }

                    _activeProcess = process;
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }

            var workerId = process.Id;

            using var readCancellation = new CancellationTokenSource();
            var outputTask = ReadBoundedAsync(process.StandardOutput, MaximumOutputCharacters, readCancellation.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, MaximumErrorCharacters, readCancellation.Token);
            var exitTask = process.WaitForExitAsync(cancellationToken);
            try
            {
                await Task.WhenAll(exitTask, outputTask, errorTask)
                    .WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                readCancellation.Cancel();
                var stopError = await StopActiveProcessAsync(killIfRunning: true).ConfigureAwait(false);
                throw new TimeoutException(
                    $"Hardware probe worker timed out: command={command}; disk={diskNumber?.ToString(CultureInfo.InvariantCulture) ?? "none"}; process={workerId}; timeout={timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s; termination={stopError ?? "confirmed"}.",
                    ex);
            }
            catch
            {
                readCancellation.Cancel();
                await StopActiveProcessAsync(killIfRunning: true).ConfigureAwait(false);
                throw;
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            var exitCode = process.ExitCode;
            await StopActiveProcessAsync(killIfRunning: false).ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Hardware probe worker failed: command={command}; disk={diskNumber?.ToString(CultureInfo.InvariantCulture) ?? "none"}; process={workerId}; exitCode={exitCode}; error={error.Trim()}.");
            }

            return JsonSerializer.Deserialize<T>(output)
                ?? throw new InvalidDataException($"Hardware probe worker returned no result: command={command}; process={workerId}.");
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<string?> StopActiveProcessAsync(bool killIfRunning)
    {
        Process? process;
        lock (_processGate)
        {
            process = _activeProcess;
        }

        if (process is null)
        {
            return null;
        }

        string? error = null;
        try
        {
            if (killIfRunning && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(KillWait).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            error = $"process={process.Id}; {ex.GetType().Name}: {ex.Message}";
        }

        if (!process.HasExited)
        {
            return error ?? $"process={process.Id} did not exit";
        }

        lock (_processGate)
        {
            if (ReferenceEquals(_activeProcess, process))
            {
                _activeProcess = null;
            }
        }

        process.Dispose();
        return null;
    }

    private void ThrowIfDisposed()
    {
        lock (_processGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string command, int? diskNumber)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "DiskMonitor.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The installed Disk Monitor worker executable could not be found.", executable);
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(command);
        if (diskNumber is not null)
        {
            startInfo.ArgumentList.Add(diskNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return startInfo;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > maximumCharacters)
            {
                throw new InvalidDataException($"Hardware probe worker output exceeded {maximumCharacters} characters.");
            }

            result.Append(buffer, 0, count);
        }

        return result.ToString();
    }
}
