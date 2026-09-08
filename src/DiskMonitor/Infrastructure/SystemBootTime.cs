namespace DiskMonitor.Infrastructure;

internal static class SystemBootTime
{
    public static DateTimeOffset GetUtc() =>
        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
}
