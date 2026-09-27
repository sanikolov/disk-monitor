namespace DiskMonitor.Infrastructure;

internal sealed class MonitorLogger
{
    private static readonly object Gate = new();
    private readonly string _logFile;
    private LogHealth _health = new(null, null);

    public MonitorLogger(string? logFile = null)
    {
        _logFile = logFile ?? AppPaths.LogFile;
    }

    public string LogFile => _logFile;

    public (DateTimeOffset? LastSuccessfulWriteUtc, string? LastWriteError) GetHealth()
    {
        // A filesystem write may itself hang. Status reads must not wait for the
        // writer's lock, or the stale-log warning would freeze with the logger.
        var health = Volatile.Read(ref _health);
        return (health.LastSuccessfulWriteUtc, health.LastWriteError);
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        lock (Gate)
        {
            try
            {
                var suffix = exception is null
                    ? string.Empty
                    : level.Equals("ERROR", StringComparison.Ordinal)
                        ? $"{Environment.NewLine}{exception}"
                        : $" {exception.GetType().Name}: {exception.Message}";
                var line = $"{DateTimeOffset.Now:O} [{level}] {message}{suffix}{Environment.NewLine}";

                var directory = Path.GetDirectoryName(_logFile);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(_logFile, line);
                var info = new FileInfo(_logFile);
                if (info.Length > 2 * 1024 * 1024)
                {
                    var archive = _logFile + ".1";
                    File.Copy(_logFile, archive, true);
                    File.WriteAllText(_logFile, string.Empty);
                }

                Volatile.Write(ref _health, new LogHealth(DateTimeOffset.UtcNow, null));
            }
            catch (Exception ex)
            {
                // Logging must not stop monitoring, but the status UI must expose
                // loss of diagnostics instead of silently presenting a healthy system.
                var previous = Volatile.Read(ref _health);
                Volatile.Write(ref _health, new LogHealth(
                    previous.LastSuccessfulWriteUtc,
                    $"{ex.GetType().Name}: {ex.Message}"));
            }
        }
    }

    private sealed record LogHealth(DateTimeOffset? LastSuccessfulWriteUtc, string? LastWriteError);
}
