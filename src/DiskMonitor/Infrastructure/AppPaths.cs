namespace DiskMonitor.Infrastructure;

internal static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskMonitor");

    public static string StateFile => Path.Combine(DataDirectory, "state.json");
    public static string LogFile => Path.Combine(DataDirectory, "monitor.log");
}
