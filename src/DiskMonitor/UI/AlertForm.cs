using System.Diagnostics;
using System.Media;
using DiskMonitor.Models;

namespace DiskMonitor.UI;

internal sealed class AlertForm : Form
{
    private const int WmSettingChange = 0x001A;
    private const int WmThemeChanged = 0x031A;

    private readonly Action<string> _acknowledge;
    private readonly Action<string> _resolve;
    private readonly Label _title;
    private readonly Label _risk;
    private readonly Label _headline;
    private readonly TextBox _details;
    private Incident _incident;
    private bool _completed;
    private bool _isDarkMode;
    private bool _themeRefreshQueued;

    public AlertForm(Incident incident, Action<string> acknowledge, Action<string> resolve)
    {
        _incident = incident;
        _acknowledge = acknowledge;
        _resolve = resolve;

        Text = "Disk Monitor - storage issue detected";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(600, 330);

        _title = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 50,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 8, 16, 0),
            Text = "Storage problem detected"
        };

        _risk = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 44,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 4, 16, 4)
        };

        _headline = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(16, 4, 16, 4)
        };

        _details = new TextBox
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = BackColor,
            Padding = new Padding(16, 4, 16, 8)
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(16, 4, 16, 8)
        };

        var acknowledgeButton = new Button
        {
            AutoSize = true,
            Text = "Acknowledge",
            DialogResult = DialogResult.OK,
            Dock = DockStyle.Right
        };
        acknowledgeButton.Click += (_, _) => AcknowledgeIncident();

        var resolveButton = new Button
        {
            AutoSize = true,
            Text = "Resolve"
        };
        resolveButton.Click += (_, _) => ResolveIncident();

        var eventViewerButton = new Button
        {
            AutoSize = true,
            Text = "Open Event Viewer",
            Dock = DockStyle.None
        };
        eventViewerButton.Click += (_, _) => OpenEventViewer();

        buttons.Controls.Add(acknowledgeButton);
        buttons.Controls.Add(resolveButton);
        buttons.Controls.Add(eventViewerButton);
        Controls.Add(_details);
        Controls.Add(_headline);
        Controls.Add(_risk);
        Controls.Add(_title);
        Controls.Add(buttons);

        AcceptButton = acknowledgeButton;
        ApplyTheme();
        RefreshIncident(incident);
        SystemSounds.Hand.Play();
    }

    public string IncidentId => _incident.Id;

    public void RefreshIncident(Incident incident)
    {
        _incident = incident;
        var risk = RiskAssessment.ForIncident(incident);
        _title.Text = risk == RiskLevel.Critical
            ? "Critical storage problem detected"
            : "Storage problem detected";
        _risk.BackColor = StatusForm.RiskColor(risk);
        _risk.Text = $"{RiskAssessment.Label(risk)} — {RiskAssessment.Guidance(risk)}";
        _headline.Text = $"{incident.DeviceName}\r\n{incident.Category} - {incident.EvidenceCount} related observation(s)";
        var evidence = incident.Evidence.LastOrDefault();
        _details.Text = $"Technical severity: {incident.Severity}\r\nFirst seen: {incident.FirstSeen.ToLocalTime():g}\r\nLast seen: {incident.LastSeen.ToLocalTime():g}\r\n\r\n{evidence?.Summary}\r\n{evidence?.Details ?? string.Empty}";
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        ApplyTheme();
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

    private void AcknowledgeIncident()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _acknowledge(_incident.Id);
        Close();
    }

    private void ResolveIncident()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _resolve(_incident.Id);
        Close();
    }

    private void ApplyTheme()
    {
        _isDarkMode = WindowsTheme.IsDarkMode();
        var surfaceColor = _isDarkMode ? Color.FromArgb(32, 32, 32) : SystemColors.Control;
        var textColor = _isDarkMode ? Color.FromArgb(240, 240, 240) : SystemColors.ControlText;
        var buttonColor = _isDarkMode ? Color.FromArgb(55, 55, 55) : SystemColors.Control;
        var borderColor = _isDarkMode ? Color.FromArgb(105, 105, 105) : SystemColors.ControlDark;

        BackColor = surfaceColor;
        ForeColor = textColor;
        foreach (var control in Descendants(this))
        {
            if (ReferenceEquals(control, _risk))
            {
                control.ForeColor = Color.White;
                continue;
            }

            control.BackColor = surfaceColor;
            control.ForeColor = textColor;
            if (control is Button button)
            {
                button.UseVisualStyleBackColor = !_isDarkMode;
                button.FlatStyle = _isDarkMode ? FlatStyle.Flat : FlatStyle.Standard;
                button.BackColor = buttonColor;
                button.FlatAppearance.BorderColor = borderColor;
            }
        }

        if (IsHandleCreated)
        {
            WindowsTheme.ApplyTitleBar(Handle, _isDarkMode);
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
            if (!IsDisposed)
            {
                ApplyTheme();
            }
        });
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void OpenEventViewer()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "eventvwr.msc",
            UseShellExecute = true
        });
    }
}
