namespace DiskMonitor.Infrastructure;

internal sealed class MonitorLogger
{
    private static readonly object Gate = new();
    private readonly string _logFile;

    public MonitorLogger(string? logFile = null)
    {
        _logFile = logFile ?? AppPaths.LogFile;
        var directory = Path.GetDirectoryName(_logFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            var suffix = exception is null
                ? string.Empty
                : level.Equals("ERROR", StringComparison.Ordinal)
                    ? $"{Environment.NewLine}{exception}"
                    : $" {exception.GetType().Name}: {exception.Message}";
            var line = $"{DateTimeOffset.Now:O} [{level}] {message}{suffix}{Environment.NewLine}";

            lock (Gate)
            {
                File.AppendAllText(_logFile, line);
                var info = new FileInfo(_logFile);
                if (info.Length > 2 * 1024 * 1024)
                {
                    var archive = _logFile + ".1";
                    File.Copy(_logFile, archive, true);
                    File.WriteAllText(_logFile, string.Empty);
                }
            }
        }
        catch
        {
            // Monitoring must continue even if its diagnostic log is unavailable.
        }
    }
}
