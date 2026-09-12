using Shared;

namespace App;

// Always-running notifier: polls the service itself every 15s and raises the
// 15-minute + time-up balloons whether or not the main window is open.
public class TrayContext : ApplicationContext
{
    public static TrayContext? Instance { get; private set; }
    private readonly NotifyIcon _icon = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 15000 };
    private MainForm? _form;
    private bool _polling;
    private string? _seenTimerKey;
    private bool _warned;
    private bool _enforceTold;

    public TrayContext()
    {
        Instance = this;
        _icon.Icon = SystemIcons.Shield;
        _icon.Text = "Steam Timer";
        _icon.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowMain());
        menu.Items.Add("Exit tray (timer keeps running, popups stop)", null, (_, _) =>
        {
            var r = MessageBox.Show(
                "Exit the tray? The timer keeps running, but you will NOT get the 15-minute / time-up popups.",
                "Steam Timer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes) { _icon.Visible = false; Application.Exit(); }
        });
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowMain();
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();
        _ = PollAsync();
    }

    public void ShowMain()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new MainForm();
            _form.FormClosed += (_, _) => _form = null;
        }
        if (!_form.Visible)
            _form.Show();
        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "status" });
            if (resp == null || !resp.Ok) return;
            _form?.UpdateFromStatus(resp);
            if (!resp.HasTimer)
            {
                _seenTimerKey = null;
                _warned = false;
                _enforceTold = false;
                SetState("No timer today — open Steam Timer to start one?", null);
                return;
            }
            var key = resp.LogicalDay + "|" + resp.DeadlineUtc.Ticks;
            if (key != _seenTimerKey)
            {
                _seenTimerKey = key;
                _warned = false;
                _enforceTold = false;
            }
            var left = resp.DeadlineUtc - DateTime.UtcNow;
            if (left > TimeSpan.Zero)
            {
                SetState($"Steam: {(int)left.TotalHours}h {left.Minutes:00}m left", left);
                if (left <= Limits.WarnBefore && !_warned)
                {
                    _warned = true;
                    Balloon("Steam closes in 15 minutes", "Save your game.");
                }
            }
            else
            {
                SetState("Steam blocked until 03:00", TimeSpan.Zero);
                if (!_enforceTold)
                {
                    _enforceTold = true;
                    Balloon("Steam time is up", "Closed Steam. Blocked until 03:00.");
                }
            }
        }
        finally { _polling = false; }
    }

    public void SetState(string tooltip, TimeSpan? left)
    {
        try { _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip; } catch { }
        if (left == null) _icon.Icon = SystemIcons.Shield;
        else if (left == TimeSpan.Zero) _icon.Icon = SystemIcons.Error;
        else if (left <= TimeSpan.FromMinutes(30)) _icon.Icon = SystemIcons.Warning;
        else _icon.Icon = SystemIcons.Shield;
    }

    public void Balloon(string title, string body)
    {
        try { _icon.ShowBalloonTip(8000, title, body, ToolTipIcon.Warning); } catch { }
    }
}
