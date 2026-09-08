using DiskMonitor.UI;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class WindowsThemeTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void RegistrySettingDeterminesDarkMode(int setting, bool expected)
    {
        Assert.Equal(expected, WindowsTheme.IsDarkModeSetting(setting));
    }

    [Fact]
    public void MissingRegistrySettingDefaultsToLightMode()
    {
        Assert.False(WindowsTheme.IsDarkModeSetting(null));
    }
}
