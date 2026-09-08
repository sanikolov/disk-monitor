namespace DiskMonitor.Models;

internal sealed class MonitorOptions
{
    public int PollIntervalMinutes { get; set; } = 30;
    public int RepeatedEventWindowMinutes { get; set; } = 15;
    public int RepeatedEventThreshold { get; set; } = 2;
    public int AlertCooldownMinutes { get; set; } = 30;
    public int AutoResolveAfterHours { get; set; } = 24;
    public int UsbFlapWindowMinutes { get; set; } = 10;
    public int UsbFlapThreshold { get; set; } = 3;
    public int DeviceNameGraceMinutes { get; set; } = 30;
    public int InitialInventoryTimeoutSeconds { get; set; } = 15;
    public int MinimumTopologyRefreshSeconds { get; set; } = 30;
}
