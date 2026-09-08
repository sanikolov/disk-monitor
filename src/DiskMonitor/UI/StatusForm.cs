using System.Globalization;
using DiskMonitor.Monitoring;
using DiskMonitor.Models;

namespace DiskMonitor.UI;

internal sealed class StatusForm : Form
{
    private const int WmSettingChange = 0x001A;
    private const int WmThemeChanged = 0x031A;

    private readonly DiskMonitorCoordinator _coordinator;
    private readonly Action _openDataFolder;
    private readonly FlowLayoutPanel _report;
    private readonly ComboBox _incidentSelector;
    private readonly Button _clearIncidentButton;
    private readonly Font _headingFont;
    private bool _isDarkMode;
    private bool _themeInitialized;
    private bool _themeRefreshQueued;
    private Color _textColor;
    private Color _secondaryTextColor;
    private Color _headingColor;

    public StatusForm(DiskMonitorCoordinator coordinator, Action openDataFolderAction)
    {
        _coordinator = coordinator;
        _openDataFolder = openDataFolderAction;
        _headingFont = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold);

        Text = "Disk Monitor status";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 500);
        Size = new Size(900, 650);

        _report = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(12),
            BackColor = SystemColors.Window,
            TabStop = true
        };
        _report.ClientSizeChanged += (_, _) => ResizeReportLines();

        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => RefreshStatus();
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        _clearIncidentButton = new Button
        {
            Text = "Clear selected alert",
            AutoSize = true,
            Enabled = false
        };
        _clearIncidentButton.Click += (_, _) => ClearSelectedIncident();
        var openDataFolderButton = new Button { Text = "Open data folder", AutoSize = true };
        openDataFolderButton.Click += (_, _) => _openDataFolder();

        _incidentSelector = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(IncidentChoice.Display)
        };

        var selectorRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(8, 8, 8, 3)
        };
        selectorRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        selectorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selectorRow.Controls.Add(
            new Label
            {
                Text = "Active incident:",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 6, 8, 3)
            },
            0,
            0);
        selectorRow.Controls.Add(_incidentSelector, 1, 0);

        var actionRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8, 3, 8, 8)
        };
        actionRow.Controls.Add(close);
        actionRow.Controls.Add(refresh);
        actionRow.Controls.Add(_clearIncidentButton);
        actionRow.Controls.Add(openDataFolderButton);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.Controls.Add(selectorRow, 0, 0);
        footer.Controls.Add(actionRow, 0, 1);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_report, 0, 0);
        layout.Controls.Add(footer, 0, 1);

        Controls.Add(layout);
        CancelButton = close;
        ApplyTheme();
    }

    public void RefreshStatus()
    {
        if (IsDisposed)
        {
            return;
        }

        var allIncidents = _coordinator.GetIncidents();
        var incidents = _coordinator.GetCurrentBootIncidents()
            .OrderBy(incident => incident.Resolved)
            .ThenByDescending(incident => incident.Severity)
            .ThenByDescending(incident => incident.LastSeen)
            .ToList();
        var overallRisk = RiskAssessment.Overall(allIncidents, _coordinator.BootTimeUtc);

        _report.SuspendLayout();
        ClearReport();
        AddHeading("DISK MONITOR");
        AddLine(string.Format(CultureInfo.CurrentCulture, "Updated: {0:g}", DateTimeOffset.Now));
        AddLine(string.Format(
            CultureInfo.CurrentCulture,
            "Assessment window: since the last Windows reboot ({0:g}).",
            _coordinator.BootTimeUtc.ToLocalTime()));
        var excludedIncidentCount = allIncidents.Count - incidents.Count;
        if (excludedIncidentCount > 0)
        {
            AddLine(
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Ignored {0} incident(s) whose latest event occurred before this reboot.",
                    excludedIncidentCount),
                _secondaryTextColor);
        }

        AddRiskLine("OVERALL RISK", overallRisk, RiskAssessment.Guidance(overallRisk));
        AddSpacer();
        AddHeading("RISK GUIDE");
        AddRiskLine(null, RiskLevel.Benign, "No active issue is currently recorded.");
        AddRiskLine(null, RiskLevel.Survivable, "The device is still responding, but retries, disconnects, or timeouts require a backup and investigation.");
        AddRiskLine(null, RiskLevel.Critical, "Corruption or hardware failure means data loss may be imminent or may already have occurred.");
        AddSpacer();
        AddHeading("DEVICES");

        var devices = _coordinator.GetDevices();
        if (devices.Count == 0)
        {
            AddLine("No physical disks could be enumerated.", Color.DarkOrange);
        }
        else
        {
            foreach (var device in devices)
            {
                AddLine(string.Format(CultureInfo.CurrentCulture, "Disk {0}: {1}", device.DiskNumber, device.DisplayName), font: _headingFont);
                AddLine(
                    string.Format(CultureInfo.CurrentCulture, "  Health={0}, Status={1}", HealthName(device.HealthStatus), string.Join(", ", device.OperationalStatus)),
                    HealthColor(device.HealthStatus));
                AddLine(string.Format(CultureInfo.CurrentCulture, "  Serial={0}, Firmware={1}, Size={2}", device.SerialNumber, device.FirmwareVersion, FormatBytes(device.SizeBytes)));
                AddLine(string.Format(CultureInfo.CurrentCulture, "  Offline={0}, SCSI={1}/{2}, DevicePath={3}", device.IsOffline, device.ScsiPort?.ToString(CultureInfo.CurrentCulture) ?? "-", device.ScsiTargetId?.ToString(CultureInfo.CurrentCulture) ?? "-", device.DevicePath));
                AddLine(string.Format(CultureInfo.CurrentCulture, "  Predict-failure={0}, Key={1}", _coordinator.GetPredictionStatus(device.Key), device.Key));
                AddSpacer(4);
            }
        }

        AddSpacer();
        AddHeading("INCIDENTS");
        if (incidents.Count == 0)
        {
            AddLine("No incidents recorded since this reboot.", HealthColor(0));
        }
        else
        {
            foreach (var incident in incidents)
            {
                var state = incident.Resolved ? "resolved" : incident.Acknowledged ? "acknowledged" : "active";
                AddRiskLine(
                    state.ToUpperInvariant(),
                    RiskAssessment.ForIncident(incident),
                    $"{incident.DeviceName} - {incident.Category}");
                AddLine(string.Format(CultureInfo.CurrentCulture, "  {0:g} to {1:g}; evidence={2}", incident.FirstSeen.ToLocalTime(), incident.LastSeen.ToLocalTime(), incident.EvidenceCount));
                AddLine($"  {incident.Evidence.LastOrDefault()?.Summary ?? string.Empty}");
                AddSpacer(4);
            }
        }

        ResizeReportLines();
        _report.AutoScrollPosition = Point.Empty;
        _report.ResumeLayout(performLayout: true);

        var selectedId = (_incidentSelector.SelectedItem as IncidentChoice)?.Id;
        var choices = incidents
            .Where(incident => !incident.Resolved)
            .Select(incident => new IncidentChoice(incident.Id, $"{incident.DeviceName}: {incident.Category} ({incident.EvidenceCount})"))
            .ToList();
        var hasActiveIncidents = choices.Count > 0;
        _incidentSelector.DataSource = hasActiveIncidents
            ? choices
            : new[] { new IncidentChoice(string.Empty, "No active incidents since this reboot") };
        _incidentSelector.Enabled = hasActiveIncidents;
        _clearIncidentButton.Enabled = hasActiveIncidents;
        if (hasActiveIncidents && !string.IsNullOrEmpty(selectedId))
        {
            var selectedChoice = choices.FirstOrDefault(choice => choice.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
            if (selectedChoice is not null)
            {
                _incidentSelector.SelectedItem = selectedChoice;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _headingFont.Dispose();
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (ApplyTheme() && _report.Controls.Count > 0)
        {
            RefreshStatus();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme();
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg is WmSettingChange or WmThemeChanged)
        {
            QueueThemeRefresh();
        }
    }

    private void ClearSelectedIncident()
    {
        if (_incidentSelector.SelectedItem is not IncidentChoice choice || string.IsNullOrEmpty(choice.Id))
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "Clear this alert from the active assessment?\r\n\r\n" +
            "This does not repair the disk or filesystem. The incident remains in history, and a new alert can be raised if Windows reports the problem again.",
            "Clear storage alert",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.OK)
        {
            return;
        }

        _coordinator.Resolve(choice.Id);
        RefreshStatus();
    }

    private void ClearReport()
    {
        while (_report.Controls.Count > 0)
        {
            _report.Controls[0].Dispose();
        }
    }

    private void AddHeading(string text) => AddLine(text, _headingColor, _headingFont, 6);

    private void AddRiskLine(string? prefix, RiskLevel risk, string description)
    {
        var label = string.IsNullOrWhiteSpace(prefix)
            ? RiskAssessment.Label(risk)
            : $"{prefix}: {RiskAssessment.Label(risk)}";
        AddLine($"{label}  —  {description}", Color.White, _headingFont, 8, RiskColor(risk));
    }

    private void AddSpacer(int height = 8)
    {
        _report.Controls.Add(new Panel
        {
            Height = height,
            Width = 1,
            Margin = Padding.Empty
        });
    }

    private void AddLine(
        string text,
        Color? foreground = null,
        Font? font = null,
        int verticalPadding = 1,
        Color? background = null)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = false,
            ForeColor = foreground ?? _textColor,
            BackColor = background ?? _report.BackColor,
            Font = font ?? SystemFonts.MessageBoxFont!,
            Padding = new Padding(background is null ? 0 : 8, verticalPadding, background is null ? 0 : 8, verticalPadding),
            Margin = new Padding(0, 0, 0, 2),
            UseMnemonic = false
        };
        _report.Controls.Add(label);
        ResizeReportLine(label);
    }

    private void ResizeReportLines()
    {
        if (_report.IsDisposed)
        {
            return;
        }

        _report.SuspendLayout();
        foreach (var label in _report.Controls.OfType<Label>())
        {
            ResizeReportLine(label);
        }

        _report.ResumeLayout(performLayout: true);
    }

    private void ResizeReportLine(Label label)
    {
        var width = Math.Max(
            200,
            _report.ClientSize.Width - _report.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
        var textWidth = Math.Max(1, width - label.Padding.Horizontal);
        var measured = TextRenderer.MeasureText(
            label.Text,
            label.Font,
            new Size(textWidth, int.MaxValue),
            TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        label.Size = new Size(width, Math.Max(label.Font.Height, measured.Height) + label.Padding.Vertical);
    }

    private bool ApplyTheme()
    {
        var darkMode = WindowsTheme.IsDarkMode();
        var changed = !_themeInitialized || darkMode != _isDarkMode;
        _themeInitialized = true;
        _isDarkMode = darkMode;

        var chromeColor = darkMode ? Color.FromArgb(32, 32, 32) : SystemColors.Control;
        var reportColor = darkMode ? Color.FromArgb(30, 30, 30) : SystemColors.Window;
        var fieldColor = darkMode ? Color.FromArgb(45, 45, 45) : SystemColors.Window;
        var buttonColor = darkMode ? Color.FromArgb(55, 55, 55) : SystemColors.Control;
        var borderColor = darkMode ? Color.FromArgb(105, 105, 105) : SystemColors.ControlDark;
        _textColor = darkMode ? Color.FromArgb(240, 240, 240) : SystemColors.WindowText;
        _secondaryTextColor = darkMode ? Color.FromArgb(185, 185, 185) : SystemColors.GrayText;
        _headingColor = darkMode ? Color.FromArgb(138, 180, 248) : Color.FromArgb(32, 57, 91);

        BackColor = chromeColor;
        ForeColor = _textColor;
        _report.BackColor = reportColor;
        ApplyControlTheme(this, chromeColor, fieldColor, buttonColor, borderColor);
        if (IsHandleCreated)
        {
            WindowsTheme.ApplyTitleBar(Handle, darkMode);
        }

        return changed;
    }

    private void ApplyControlTheme(
        Control parent,
        Color chromeColor,
        Color fieldColor,
        Color buttonColor,
        Color borderColor)
    {
        foreach (Control control in parent.Controls)
        {
            if (ReferenceEquals(control, _report))
            {
                control.BackColor = _report.BackColor;
                control.ForeColor = _textColor;
                continue;
            }

            switch (control)
            {
                case Button button:
                    button.UseVisualStyleBackColor = !_isDarkMode;
                    button.FlatStyle = _isDarkMode ? FlatStyle.Flat : FlatStyle.Standard;
                    button.BackColor = buttonColor;
                    button.ForeColor = _textColor;
                    button.FlatAppearance.BorderColor = borderColor;
                    break;
                case ComboBox comboBox:
                    comboBox.FlatStyle = _isDarkMode ? FlatStyle.Flat : FlatStyle.Standard;
                    comboBox.BackColor = fieldColor;
                    comboBox.ForeColor = _textColor;
                    break;
                default:
                    control.BackColor = chromeColor;
                    control.ForeColor = _textColor;
                    break;
            }

            ApplyControlTheme(control, chromeColor, fieldColor, buttonColor, borderColor);
        }
    }

    private void QueueThemeRefresh()
    {
        if (_themeRefreshQueued || !IsHandleCreated || IsDisposed)
        {
            return;
        }

        _themeRefreshQueued = true;
        BeginInvoke(() =>
        {
            _themeRefreshQueued = false;
            if (IsDisposed)
            {
                return;
            }

            ApplyTheme();
            RefreshStatus();
        });
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes == 0)
        {
            return "unknown";
        }

        var value = (double)bytes;
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", value, units[unit]);
    }

    private static string HealthName(ushort value) => value switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        5 => "Unknown",
        _ => $"Status {value}"
    };

    internal static Color RiskColor(RiskLevel risk) => risk switch
    {
        RiskLevel.Benign => Color.FromArgb(33, 130, 73),
        RiskLevel.Survivable => Color.FromArgb(190, 105, 0),
        RiskLevel.Critical => Color.FromArgb(190, 35, 45),
        _ => SystemColors.WindowText
    };

    private Color HealthColor(ushort healthStatus) => healthStatus switch
    {
        0 when _isDarkMode => Color.FromArgb(111, 207, 151),
        1 when _isDarkMode => Color.FromArgb(255, 190, 92),
        2 when _isDarkMode => Color.FromArgb(255, 120, 130),
        0 => RiskColor(RiskLevel.Benign),
        1 => RiskColor(RiskLevel.Survivable),
        2 => RiskColor(RiskLevel.Critical),
        _ => _secondaryTextColor
    };

    private sealed record IncidentChoice(string Id, string Display);
}
