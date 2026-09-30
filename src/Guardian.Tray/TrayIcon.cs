using System.Diagnostics;
using System.Reflection;
using Guardian.Contracts;

namespace Guardian.Tray;

/// <summary>
/// The always-visible NotifyIcon and its context menu. There is deliberately no "Exit" item: the tray is
/// overt UI the child is meant to see, and the service relaunches it within 10 s anyway (recorded as a
/// tray_relaunch event), so an Exit button would only create noise.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int MaxTooltip = 63;   // classic Shell_NotifyIcon limit; keep to it for safety

    private readonly NotifyIcon _icon;
    private readonly Dictionary<TrayState, Icon> _icons = new();
    private TrayStatus _status = new();
    private TrayState? _shownState;
    private string? _shownTooltip;
    private CollectsForm? _collects;
    private TimeRequestForm? _timeRequest;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open my activity", null, (_, _) => OpenActivity());
        menu.Items.Add("What Guardian collects", null, (_, _) => ShowCollects());
        menu.Items.Add("Request more time…", null, (_, _) => ShowTimeRequest());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("About Guardian", null, (_, _) => ShowAbout());

        _icon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenActivity(); };
        _icon.BalloonTipClicked += (_, _) => OpenActivity();
        Apply(new TrayStatus { State = TrayState.ServiceDown, Tooltip = "Guardian isn't running" });
    }

    /// <summary>Default child URL when the service has not told us one (loopback HTTP child view).</summary>
    public static string DefaultChildUrl => $"http://localhost:{Ports.Local}/me";

    public void Apply(TrayStatus status)
    {
        _status = status;
        if (_shownState != status.State)
        {
            if (!_icons.TryGetValue(status.State, out var icon))
                _icons[status.State] = icon = IconFactory.Create(status.State);
            _icon.Icon = icon;
            _shownState = status.State;
        }

        var tip = string.IsNullOrWhiteSpace(status.Tooltip) ? Names.Product : status.Tooltip;
        if (tip.Length > MaxTooltip) tip = tip[..(MaxTooltip - 1)] + "…";
        if (tip != _shownTooltip)
        {
            _icon.Text = tip;
            _shownTooltip = tip;
        }
    }

    /// <summary>Balloon for a notice. No sound: the balloon uses the default (silent) info style.</summary>
    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon.BalloonTipTitle = string.IsNullOrWhiteSpace(title) ? Names.Product : title;
            _icon.BalloonTipText = string.IsNullOrWhiteSpace(message) ? " " : message;
            _icon.BalloonTipIcon = ToolTipIcon.None;
            _icon.ShowBalloonTip(10_000);
        }
        catch (Exception ex)
        {
            Log.Error("Balloon failed", ex);
        }
    }

    private string ChildUrl => string.IsNullOrWhiteSpace(_status.ChildUrl) ? DefaultChildUrl : _status.ChildUrl;

    private void OpenActivity()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ChildUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Open activity failed", ex);
        }
    }

    private void ShowCollects()
    {
        if (_collects is { IsDisposed: false })
        {
            _collects.Activate();
            return;
        }
        var text = NoticeText.ToPlainText(NoticeText.Render(_status.MonitoredUser, ChildUrl));
        _collects = new CollectsForm(text);
        _collects.Show();
    }

    private void ShowTimeRequest()
    {
        if (_timeRequest is { IsDisposed: false })
        {
            _timeRequest.Activate();
            return;
        }
        _timeRequest = new TimeRequestForm();
        _timeRequest.Show();
    }

    private static void ShowAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        MessageBox.Show(
            $"Guardian tray {version}\n\nGuardian is visible by design.\nThis icon stays in the tray whenever Guardian is running, and everything it records is shown to you at the same address your parent sees.",
            "About Guardian", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
    }
}
