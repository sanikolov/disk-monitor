using DiskMonitor.Events;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class DeviceChangeMonitorTests
{
    [Fact]
    public void NotificationWindowDoesNotConflictWithWinFormsControlClasses()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var monitor = new DeviceChangeMonitor();
                monitor.Start();
                using var label = new Label();
                _ = label.Handle;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);

        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The WinForms handle-creation test timed out.");
        Assert.Null(failure);
    }
}
