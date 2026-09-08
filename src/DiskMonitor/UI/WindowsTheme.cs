using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DiskMonitor.UI;

internal static class WindowsTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

    public static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return IsDarkModeSetting(key?.GetValue(AppsUseLightThemeValue));
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            return false;
        }
    }

    internal static bool IsDarkModeSetting(object? setting) => setting is int value && value == 0;

    public static void ApplyTitleBar(nint windowHandle, bool darkMode)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == 0)
        {
            return;
        }

        var enabled = darkMode ? 1 : 0;
        if (DwmSetWindowAttribute(
                windowHandle,
                DwmwaUseImmersiveDarkMode,
                ref enabled,
                sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(
                windowHandle,
                DwmwaUseImmersiveDarkModeBefore20H1,
                ref enabled,
                sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
