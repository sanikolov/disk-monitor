using DiskMonitor.UI;
using DiskMonitor.Infrastructure;
using DiskMonitor.Storage;

namespace DiskMonitor;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\DiskMonitor.SingleInstance";
    private const string ShowStatusEventName = @"Local\DiskMonitor.ShowStatus";

    [STAThread]
    private static int Main(string[] args)
    {
        // Probe children bypass the tray singleton and UI entirely. The parent can
        // terminate a stuck native query without losing event-log monitoring.
        if (HardwareProbeWorker.TryRun(args, out var workerExitCode))
        {
            return workerExitCode;
        }

        var logger = new MonitorLogger();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => LogUnhandled("The UI thread raised an unhandled exception.", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogUnhandled(
            args.IsTerminating
                ? "The process is terminating because of an unhandled exception."
                : "A background thread raised an unhandled exception.",
            args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogUnhandled("An unobserved task exception occurred.", args.Exception);
            args.SetObserved();
        };

        using var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        using var showStatusSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowStatusEventName, out _);
        if (!createdNew)
        {
            showStatusSignal.Set();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        logger.Info($"Disk Monitor process starting (process ID {Environment.ProcessId}).");
        Application.Run(new TrayApplicationContext(showStatusSignal, logger));
        logger.Info("Disk Monitor UI message loop ended normally.");
        return 0;
    }

    private static void LogUnhandled(string message, Exception? exception)
    {
        try
        {
            new MonitorLogger().Error(message, exception);
        }
        catch
        {
            // Logging must not become another unhandled exception.
        }
    }
}
