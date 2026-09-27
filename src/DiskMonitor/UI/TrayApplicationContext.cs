using System.Diagnostics;
using DiskMonitor.Events;
using DiskMonitor.Infrastructure;
using DiskMonitor.Monitoring;
using DiskMonitor.Models;

namespace DiskMonitor.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly DiskMonitorCoordinator _coordinator;
    private readonly MonitorLogger _logger;
    private readonly SynchronizationContext _uiContext;
    private readonly RegisteredWaitHandle _showStatusRegistration;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly List<AlertForm> _alertForms = new();
    private Icon? _trayIcon;
    private TrayIconState? _currentTrayState;
    private StatusForm? _statusForm;
    private bool _exiting;
    private bool _monitoringWarningShown;

    public TrayApplicationContext(EventWaitHandle showStatusSignal, MonitorLogger logger)
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _showStatusRegistration = ThreadPool.RegisterWaitForSingleObject(
            showStatusSignal,
            (_, _) => _uiContext.Post(_ => ShowStatus(), null),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        // Share the startup logger so a failed startup write remains visible in status.
        _logger = logger;
        _logger.Info($"Tray application context started (process ID {Environment.ProcessId}).");
        var options = new MonitorOptions();
        var stateStore = new JsonStateStore(_logger);
        _coordinator = new DiskMonitorCoordinator(_logger, stateStore, options);
        _coordinator.AlertRaised += OnAlertRaised;
        _coordinator.IncidentsChanged += OnIncidentsChanged;
        _coordinator.MonitoringChanged += OnMonitoringChanged;
        _coordinator.ShutdownRequested += OnShutdownRequested;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open status", null, (_, _) => ShowStatus());
        menu.Items.Add("Check now", null, (_, _) => _coordinator.CheckNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open data folder", null, (_, _) => OpenDataFolder());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication("The user selected Exit from the tray menu."));

        _notifyIcon = new NotifyIcon
        {
            Text = "Disk Monitor",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatus();
        SetTrayIcon(TrayIconState.Healthy);

        // Inventory reads may stall without returning. The UI timer keeps the tray
        // and the open status window honest about the age of their source data.
        _statusTimer = new System.Windows.Forms.Timer { Interval = 15000 };
        _statusTimer.Tick += (_, _) => UpdateTrayState();
        _coordinator.Start();
        _statusTimer.Start();
        UpdateTrayState();
    }

    private void OnAlertRaised(object? sender, IncidentRaisedEventArgs args)
    {
        _uiContext.Post(_ => ShowAlert(args.Incident), null);
    }

    private void OnIncidentsChanged(object? sender, EventArgs args)
    {
        _uiContext.Post(_ => UpdateTrayState(refreshReport: true), null);
    }

    private void OnMonitoringChanged(object? sender, EventArgs args) =>
        _uiContext.Post(_ => UpdateTrayState(), null);

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs args)
    {
        var reason = args.Reason switch
        {
            ShutdownRequestReason.WindowClose => "The hidden device-notification window received WM_CLOSE.",
            ShutdownRequestReason.SessionEnding => "Windows is ending the user session.",
            _ => "Windows requested application shutdown."
        };
        _logger.Info($"Shutdown requested: {reason}");
        _uiContext.Post(_ => ExitApplication(reason), null);
    }

    private void ShowAlert(Incident incident)
    {
        if (_exiting)
        {
            return;
        }

        var existing = _alertForms.FirstOrDefault(form => form.IncidentId.Equals(incident.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.RefreshIncident(incident);
            RestoreAndActivate(existing);
            return;
        }

        var form = new AlertForm(incident, _coordinator.Acknowledge, _coordinator.Resolve);
        form.FormClosed += (_, _) => _alertForms.Remove(form);
        _alertForms.Add(form);
        RestoreAndActivate(form);
        try
        {
            _notifyIcon.ShowBalloonTip(
                10000,
                $"{RiskAssessment.Label(RiskAssessment.ForIncident(incident))}: storage issue since reboot",
                $"{incident.DeviceName}: {incident.Evidence.LastOrDefault()?.Summary ?? incident.Headline} Last reported {incident.LastSeen.ToLocalTime():g}.",
                ToolTipIcon.Error);
        }
        catch (Exception ex)
        {
            // The topmost alert window is primary; a shell notification is best effort.
            _logger.Warn("Windows could not display the storage alert notification.", ex);
        }
    }

    private void ShowStatus()
    {
        if (_exiting)
        {
            return;
        }

        if (_statusForm is null || _statusForm.IsDisposed)
        {
            _statusForm = new StatusForm(_coordinator, OpenDataFolder);
            _statusForm.FormClosed += (_, _) => _statusForm = null;
        }

        RestoreAndActivate(_statusForm);
        _statusForm.RefreshStatus();
    }

    private void UpdateTrayState(bool refreshReport = false)
    {
        if (_exiting)
        {
            return;
        }

        var active = _coordinator.GetCurrentBootIncidents(activeOnly: true);
        var monitoring = _coordinator.GetMonitoringStatus();
        var degraded = monitoring.IsDegraded(DateTimeOffset.UtcNow);
        SetTrayIcon(active.Any(incident => !incident.Acknowledged)
            ? TrayIconState.Error
            : active.Count > 0 || degraded
                ? TrayIconState.Warning
                : TrayIconState.Healthy);
        _notifyIcon.Text = degraded
            ? "Disk Monitor: monitoring degraded"
            : active.Count == 0 ? "Disk Monitor: no active issues" : $"Disk Monitor: {active.Count} active issue(s)";
        if (degraded && !_monitoringWarningShown)
        {
            _monitoringWarningShown = true;
            try
            {
                _notifyIcon.ShowBalloonTip(10000, "Disk Monitor needs attention", "A monitoring source is stale or unavailable. Open status for details.", ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                _logger.Warn("Windows could not display the monitoring warning notification.", ex);
            }
        }
        else if (!degraded)
        {
            _monitoringWarningShown = false;
        }
        foreach (var form in _alertForms.ToList())
        {
            var updated = active.FirstOrDefault(incident => incident.Id.Equals(form.IncidentId, StringComparison.OrdinalIgnoreCase));
            if (updated is null)
            {
                form.Close();
            }
            else
            {
                form.RefreshIncident(updated);
            }
        }

        if (refreshReport)
        {
            _statusForm?.RefreshStatus();
        }
        else
        {
            _statusForm?.RefreshMonitoringIfChanged();
        }
    }

    private void SetTrayIcon(TrayIconState state)
    {
        if (_currentTrayState == state)
        {
            return;
        }

        var replacement = TrayIconFactory.Create(state);
        var previous = _trayIcon;
        _trayIcon = replacement;
        _currentTrayState = state;
        _notifyIcon.Icon = replacement;
        previous?.Dispose();
    }

    private void OpenDataFolder()
    {
        try
        {
            var directory = Directory.CreateDirectory(AppPaths.DataDirectory).FullName;
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(directory);
            if (Process.Start(startInfo) is null)
            {
                throw new InvalidOperationException("Windows Explorer did not start.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("The Disk Monitor data folder could not be opened.", ex);
            MessageBox.Show(
                $"Disk Monitor could not open its data folder.\r\n\r\n{AppPaths.DataDirectory}\r\n\r\n{ex.Message}",
                "Disk Monitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void RestoreAndActivate(Form form)
    {
        if (!form.Visible)
        {
            form.Show();
        }

        form.WindowState = FormWindowState.Normal;
        form.ShowInTaskbar = true;
        form.BringToFront();
        form.Activate();
    }

    private void ExitApplication(string reason)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _logger.Info($"Disk Monitor is exiting. Reason: {reason}");
        _showStatusRegistration.Unregister(null);
        foreach (var form in _alertForms.ToList())
        {
            form.Close();
        }

        _statusForm?.Close();
        _coordinator.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _logger.Info("Disk Monitor cleanup completed; ending the UI message loop.");
        ExitThread();
    }
}
