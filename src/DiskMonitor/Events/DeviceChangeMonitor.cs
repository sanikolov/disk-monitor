using System.Windows.Forms;

namespace DiskMonitor.Events;

internal sealed class DeviceChangeEventArgs : EventArgs
{
    public DeviceChangeEventArgs(bool isRemoval)
    {
        IsRemoval = isRemoval;
    }

    public bool IsRemoval { get; }
}

internal enum ShutdownRequestReason
{
    WindowClose,
    SessionEnding
}

internal sealed class ShutdownRequestedEventArgs : EventArgs
{
    public ShutdownRequestedEventArgs(ShutdownRequestReason reason)
    {
        Reason = reason;
    }

    public ShutdownRequestReason Reason { get; }
}

internal sealed class DeviceChangeMonitor : IDisposable
{
    private DeviceNotificationWindow? _window;

    public event EventHandler<DeviceChangeEventArgs>? DeviceChanged;
    public event EventHandler<ShutdownRequestedEventArgs>? ShutdownRequested;

    public void Start()
    {
        _window = new DeviceNotificationWindow(this);
    }

    public void Dispose()
    {
        _window?.DestroyHandle();
        _window = null;
    }

    private void Raise(bool isRemoval) => DeviceChanged?.Invoke(this, new DeviceChangeEventArgs(isRemoval));

    private void RaiseShutdownRequested(ShutdownRequestReason reason) =>
        ShutdownRequested?.Invoke(this, new ShutdownRequestedEventArgs(reason));

    private sealed class DeviceNotificationWindow : NativeWindow
    {
        private const int WmClose = 0x0010;
        private const int WmQueryEndSession = 0x0011;
        private const int WmEndSession = 0x0016;
        private const int WmDeviceChange = 0x0219;
        private const int DbtDevNodesChanged = 0x0007;
        private const int DbtDeviceArrival = 0x8000;
        private const int DbtDeviceRemoveComplete = 0x8004;

        private readonly DeviceChangeMonitor _owner;

        public DeviceNotificationWindow(DeviceChangeMonitor owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams
            {
                Caption = "DiskMonitor.DeviceNotifications"
            });
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmQueryEndSession)
            {
                message.Result = new IntPtr(1);
                return;
            }

            if (message.Msg == WmEndSession)
            {
                if (message.WParam != IntPtr.Zero)
                {
                    _owner.RaiseShutdownRequested(ShutdownRequestReason.SessionEnding);
                }

                return;
            }

            if (message.Msg == WmClose)
            {
                _owner.RaiseShutdownRequested(ShutdownRequestReason.WindowClose);
                return;
            }

            if (message.Msg == WmDeviceChange)
            {
                var eventType = message.WParam.ToInt32();
                if (eventType == DbtDeviceArrival || eventType == DbtDevNodesChanged)
                {
                    _owner.Raise(false);
                }
                else if (eventType == DbtDeviceRemoveComplete)
                {
                    _owner.Raise(true);
                }
            }

            base.WndProc(ref message);
        }
    }
}
