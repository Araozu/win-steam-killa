using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using Shared;

namespace App;

// Always-running notifier: polls the service every 15s and raises balloons
// whether or not the main window is open. Icon/menu live here (WinForms
// NotifyIcon hosted in the WPF app); the window is display-only.
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Action _showMain;
    private readonly MainWindow _window;
    private readonly ToolStripMenuItem _borrowMenuItem;
    private readonly ToolStripMenuItem _dotaBorrowMenuItem;
    private bool _polling;
    private bool _disposed;
    private string? _seenDay;
    private bool _warned;
    private bool _enforceTold;
    private string? _seenSessionKey;
    private bool _sessionWarned;
    private string? _seenCooldownKey;
    private bool _cooldownTold;
    private bool _wasCooldown;
    private string? _seenDotaWeek;
    private bool _dotaWarned;
    private bool _dotaTold;
    private string? _seenForbiddenDay;
    private bool _forbiddenTold;
    private string? _seenWindowKey;
    private bool _windowTold;

    public TrayIcon(Action showMain, MainWindow window)
    {
        _showMain = showMain;
        _window = window;
        _icon.Icon = SystemIcons.Shield;
        _icon.Text = "Steam Timer";
        _icon.Visible = true;
        var menu = new ContextMenuStrip { ShowItemToolTips = true };
        menu.Items.Add("Open", null, (_, _) => _showMain());
        menu.Items.Add("Recheck now (ask service what is open)", null, async (_, _) => await _window.RecheckAsync(notify: true));
        menu.Items.Add("Force close Steam + games (stop timer)", null, async (_, _) => await _window.ForceCloseAsync(notify: true));
        _borrowMenuItem = new ToolStripMenuItem("Borrow from tomorrow") { Enabled = false };
        _borrowMenuItem.Click += async (_, _) => await _window.BorrowAsync(dota: false, notify: true);
        menu.Items.Add(_borrowMenuItem);
        _dotaBorrowMenuItem = new ToolStripMenuItem("Borrow from next week") { Enabled = false };
        _dotaBorrowMenuItem.Click += async (_, _) => await _window.BorrowAsync(dota: true, notify: true);
        menu.Items.Add(_dotaBorrowMenuItem);
        menu.Items.Add("Exit tray (timer keeps running, popups stop)", null, (_, _) =>
        {
            var r = System.Windows.MessageBox.Show(
                "Exit the tray? The timer keeps running, but you will NOT get the 15-minute / time-up popups.",
                "Steam Timer", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (r == System.Windows.MessageBoxResult.Yes)
            {
                _icon.Visible = false;
                System.Windows.Application.Current.Shutdown();
            }
        });
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => _showMain();
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();
        _ = PollAsync();
    }

    private static string Hm(double totalSeconds)
    {
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds)) return "—";
        if (totalSeconds < 0) totalSeconds = 0;
        if (totalSeconds > TimeSpan.MaxValue.TotalSeconds) totalSeconds = TimeSpan.MaxValue.TotalSeconds;
        var t = TimeSpan.FromSeconds(totalSeconds);
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }

    public void SetState(string tooltip, TimeSpan? left)
    {
        if (_disposed) return;
        try { _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip; } catch { }
        if (left == null) _icon.Icon = SystemIcons.Shield;
        else if (left == TimeSpan.Zero) _icon.Icon = SystemIcons.Error;
        else if (left <= TimeSpan.FromMinutes(30)) _icon.Icon = SystemIcons.Warning;
        else _icon.Icon = SystemIcons.Shield;
    }

    // Called by MainWindow.RefreshCountdown for the live tooltip (no balloons here).
    public void SetStateFromWindow(PipeResponse r, double remaining, double cooldownLeft)
    {
        if (_disposed) return;
        if (r.OutsidePlayHours)
            SetState($"Outside play hours — back {r.NextPlayAllowedLocal:HH:mm}", TimeSpan.Zero);
        else if (r.Enforcing || remaining <= 0)
            SetState("Steam blocked until 03:00", TimeSpan.Zero);
        else if (r.InCooldown)
        {
            SetState(cooldownLeft > 0 ? $"Cooldown: {Limits.FormatHm(TimeSpan.FromSeconds(cooldownLeft))} left"
                : "Checking cooldown with the service…", TimeSpan.Zero);
        }
        else
        {
            var left = TimeSpan.FromSeconds(remaining);
            string tip = (r.Running ? $"Steam: {Hm(remaining)} left today" : $"Paused: {Hm(remaining)} left today")
                + (r.WindowEndsLocal != null ? $" • until {r.WindowEndsLocal:HH:mm}" : "")
                + (r.DotaForbidden ? " • Dota forbidden M/W/F" : r.DotaEnforcing ? " • Dota done" : "");
            SetState(tip, left);
        }
    }

    internal void SetBorrowOffer(bool dota, string label, bool enabled, string tooltip)
    {
        if (_disposed) return;
        var item = dota ? _dotaBorrowMenuItem : _borrowMenuItem;
        item.Text = label;
        item.Enabled = enabled;
        item.ToolTipText = tooltip;
    }

    public void Balloon(string title, string body)
    {
        try { _icon.ShowBalloonTip(8000, title, body, ToolTipIcon.Warning); } catch { }
    }

    private async Task PollAsync()
    {
        if (_polling || _disposed || _window.ActionInProgress) return;
        _polling = true;
        try
        {
            var (resp, applied) = await _window.SendToServiceAsync("status");
            if (_disposed || !applied) return;
            if (resp == null || !resp.Ok)
            {
                SetState("Service unreachable — is SteamTimer running?", null);
                return;
            }
            if (resp.LogicalDay != _seenDay)
            {
                _seenDay = resp.LogicalDay;
                _warned = false; _enforceTold = false;
                _seenSessionKey = null; _sessionWarned = false;
                _seenCooldownKey = null; _cooldownTold = false;
            }
            if (resp.DotaWeekStart != _seenDotaWeek) { _seenDotaWeek = resp.DotaWeekStart; _dotaWarned = false; _dotaTold = false; }
            if (resp.LogicalDay != _seenForbiddenDay) { _seenForbiddenDay = resp.LogicalDay; _forbiddenTold = false; }
            if (resp.OutsidePlayHours)
            {
                SetState($"Outside play hours — back {resp.NextPlayAllowedLocal:HH:mm}", TimeSpan.Zero);
                var wkey = resp.NextPlayAllowedLocal.ToString("yyyy-MM-dd HH:mm");
                if (wkey != _seenWindowKey) { _seenWindowKey = wkey; _windowTold = false; }
                if (!_windowTold)
                {
                    _windowTold = true;
                    string extra = (resp.Enforcing || resp.RemainingSeconds <= 0) ? " Daily budget also used up." : "";
                    Balloon("Outside play hours", $"Steam blocked ({Limits.PlayHoursSummary}, weekends free). Back at {resp.NextPlayAllowedLocal:HH:mm}.{extra}");
                }
                _wasCooldown = false;
                return;
            }
            if (resp.Enforcing || resp.RemainingSeconds <= 0)
            {
                SetState("Steam blocked until 03:00", TimeSpan.Zero);
                if (!_enforceTold) { _enforceTold = true; Balloon("Steam time is up", "Daily budget used. Blocked until 03:00."); }
                _wasCooldown = false;
                return;
            }
            if (resp.InCooldown)
            {
                var key = resp.CooldownUntilUtc.Ticks.ToString();
                if (key != _seenCooldownKey) { _seenCooldownKey = key; _cooldownTold = false; }
                if (!_cooldownTold)
                {
                    _cooldownTold = true;
                    string until = resp.CooldownUntilUtc == default ? "" : $" Next session after {resp.CooldownUntilUtc.ToLocalTime():HH:mm}.";
                    Balloon("Cooldown — Steam blocked", $"{Hm(resp.CooldownRemainingSeconds)} until the next session.{until}");
                }
                _wasCooldown = true;
                return;
            }
            if (_wasCooldown) { _wasCooldown = false; Balloon("Cooldown over", "You can play again."); }
            double remSafe = double.IsNaN(resp.RemainingSeconds) || double.IsInfinity(resp.RemainingSeconds) ? 0 : Math.Max(0, resp.RemainingSeconds);
            var left = TimeSpan.FromSeconds(Math.Min(remSafe, TimeSpan.MaxValue.TotalSeconds));
            string tip = (resp.Running ? $"Steam: {Hm(resp.RemainingSeconds)} left" : $"Paused: {Hm(resp.RemainingSeconds)} left")
                + (resp.WindowEndsLocal != null ? $" • until {resp.WindowEndsLocal:HH:mm}" : "")
                + (resp.DotaForbidden ? " • Dota forbidden M/W/F" : resp.DotaEnforcing ? " • Dota done" : "");
            bool sessionEnding = resp.Running && resp.SessionRemainingSeconds < resp.RemainingSeconds &&
                resp.SessionRemainingSeconds <= Limits.WarnBefore.TotalSeconds && resp.SessionRemainingSeconds > 0;
            bool dotaLow = resp.DotaRunning && !resp.DotaForbidden && !resp.DotaEnforcing &&
                resp.DotaRemainingSeconds <= Limits.WarnBefore.TotalSeconds && resp.DotaRemainingSeconds > 0;
            var iconLeft = (sessionEnding || dotaLow) && left > TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(29) : left;
            SetState(tip, iconLeft);
            if (resp.RemainingSeconds <= Limits.WarnBefore.TotalSeconds && !_warned)
            {
                _warned = true;
                Balloon("Steam closes in 15 minutes", $"{Hm(resp.RemainingSeconds)} of today's budget left. Save your game.");
            }
            if (sessionEnding)
            {
                var key = resp.LogicalDay + "|" + resp.StartUtc.Ticks;
                if (key != _seenSessionKey) { _seenSessionKey = key; _sessionWarned = false; }
                if (!_sessionWarned) { _sessionWarned = true; Balloon("Session ending", $"This session ends in {Hm(resp.SessionRemainingSeconds)}, then a cooldown. Save your game."); }
            }
            else { _seenSessionKey = null; _sessionWarned = false; }
            if (resp.DotaForbidden)
            {
                if (!_forbiddenTold) { _forbiddenTold = true; Balloon("Dota 2 forbidden on Mon / Wed / Fri", "No Dota today — back tomorrow. Steam + other games continue."); }
            }
            else if (resp.DotaEnforcing)
            {
                if (!_dotaTold)
                {
                    _dotaTold = true;
                    Balloon("Dota 2 time is up", resp.WorkoutGatesToday && resp.WorkoutDotaEarnedSeconds <= 0
                        ? "No workout credit — Dota workout allowance is empty. Other games continue."
                        : "Weekly Dota budget used. Dota is blocked until Monday 03:00; other games continue.");
                }
            }
            else if (dotaLow && !_dotaWarned)
            {
                _dotaWarned = true;
                Balloon("Dota 2 closes in 15 minutes", $"{Hm(resp.DotaRemainingSeconds)} of the weekly Dota budget left. Other games are unaffected.");
            }
        }
        catch { }
        finally { _polling = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _poll.Stop(); } catch { }
        try { _icon.Visible = false; } catch { }
        try { _icon.ContextMenuStrip?.Dispose(); } catch { }
        try { _icon.Dispose(); } catch { }
    }
}
