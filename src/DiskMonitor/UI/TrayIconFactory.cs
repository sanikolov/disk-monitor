using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DiskMonitor.UI;

internal enum TrayIconState
{
    Healthy,
    Warning,
    Error
}

internal static class TrayIconFactory
{
    public static Icon Create(TrayIconState state)
    {
        var iconSize = SystemInformation.SmallIconSize;
        var scale = Math.Max(iconSize.Width, iconSize.Height) / 16f;
        using var bitmap = new Bitmap(iconSize.Width, iconSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.ScaleTransform(scale, scale);
            graphics.Clear(Color.Transparent);

            using var diskBrush = new SolidBrush(Color.FromArgb(235, 235, 240));
            using var diskPen = new Pen(Color.FromArgb(55, 55, 65), 1.2f);
            graphics.FillEllipse(diskBrush, 2, 3, 12, 10);
            graphics.DrawEllipse(diskPen, 2, 3, 12, 10);

            using var hubBrush = new SolidBrush(Color.FromArgb(70, 75, 90));
            graphics.FillEllipse(hubBrush, 6, 6, 4, 4);

            var statusColor = state switch
            {
                TrayIconState.Warning => Color.FromArgb(245, 166, 35),
                TrayIconState.Error => Color.FromArgb(210, 45, 45),
                _ => Color.FromArgb(45, 170, 85)
            };
            using var statusBrush = new SolidBrush(statusColor);
            using var statusPen = new Pen(Color.White, 1);
            graphics.FillEllipse(statusBrush, 10, 0, 6, 6);
            graphics.DrawEllipse(statusPen, 10, 0, 6, 6);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var nativeIcon = Icon.FromHandle(handle);
            return (Icon)nativeIcon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
