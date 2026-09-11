namespace App;

// Quiet tray: tooltip shows remaining time, balloon only for 15m warning + kill.
public class TrayContext : ApplicationContext
{
    public static TrayContext? Instance { get; private set; }
    private readonly NotifyIcon _icon = new();
    private TimeSpan? _lastLeft;

    public TrayContext()
    {
        Instance = this;
        _icon.Icon = SystemIcons.Shield;
        _icon.Text = "Steam Timer";
        _icon.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => new MainForm().Show());
        menu.Items.Add("Exit tray (timer keeps running)", null, (_, _) => { _icon.Visible = false; Application.Exit(); });
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => new MainForm().Show();
    }

    public void SetState(string tooltip, TimeSpan? left)
    {
        _lastLeft = left;
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
