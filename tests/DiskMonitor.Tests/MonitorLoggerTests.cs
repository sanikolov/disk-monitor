using DiskMonitor.Infrastructure;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class MonitorLoggerTests
{
    [Fact]
    public void SuccessfulWriteReportsItsTimeAndClearsTheError()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "monitor.log");
            var logger = new MonitorLogger(path);
            var before = DateTimeOffset.UtcNow;

            logger.Info("Test diagnostic entry");

            var after = DateTimeOffset.UtcNow;
            var health = logger.GetHealth();
            Assert.Equal(path, logger.LogFile);
            Assert.NotNull(health.LastSuccessfulWriteUtc);
            Assert.InRange(health.LastSuccessfulWriteUtc.Value, before, after);
            Assert.Null(health.LastWriteError);
            Assert.Contains("Test diagnostic entry", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnwritableLogPathIsReportedWithoutThrowing()
    {
        var directory = NewTemporaryDirectory();
        try
        {
            // An existing directory cannot be opened as a log file, regardless of ACLs.
            var logger = new MonitorLogger(directory);

            logger.Info("This write must fail");

            var health = logger.GetHealth();
            Assert.Null(health.LastSuccessfulWriteUtc);
            Assert.False(string.IsNullOrWhiteSpace(health.LastWriteError));
            Assert.Contains("Exception", health.LastWriteError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string NewTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiskMonitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
