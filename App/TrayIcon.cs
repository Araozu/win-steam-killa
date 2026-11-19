using System.Drawing;
using System.Windows.Forms;
using Microsoft.UI.Xaml;
using Shared;

namespace App;

// Always-running notifier: polls the service every 15s and raises balloons
// whether or not the main window is open. The icon and menu are a WinForms
// NotifyIcon hosted in the WinUI app. Tooltip and icon come only from Render,
// which the window calls on every refresh.
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Action _showMain;
    private readonly Func<MainWindow?> _getWindow;
    private readonly ActionGate _gate;
    private readonly ToolStripMenuItem _borrowMenuItem;
    private readonly ToolStripMenuItem _dotaBorrowMenuItem;
    private bool _polling;
    private bool _disposed;
    private string _shownText = "";
    private TrayLevel? _shownLevel;
    // Balloon de-duplication. Daily and Dota keys include the budget so a
    // larger allowance (borrow, new workout credit) is warned about again.
    private string? _seenDay;
    private string? _seenBudgetKey;
    private bool _warned;
    private bool _enforceTold;
    private string? _seenSessionKey;
    private bool _sessionWarned;
    private string? _seenCooldownKey;
    private bool _cooldownTold;
    private bool _wasCooldown;
    private string? _seenDotaKey;
    private bool _dotaWarned;
    private bool _dotaTold;
    private string? _seenForbiddenDay;
    private bool _forbiddenTold;
    private string? _seenWindowKey;
    private bool _windowTold;
    private string? _windowWarnKey;

    internal TrayIcon(Action showMain, Func<MainWindow?> getWindow, ActionGate gate)
    {
        _showMain = showMain;
        _getWindow = getWindow;
        _gate = gate;
        _icon.Icon = SystemIcons.Shield;
        _icon.Text = "Steam Timer";
        _icon.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => _showMain());
        menu.Items.Add("Recheck now (ask service what is open)", null, async (_, _) => await RecheckAsync());
        menu.Items.Add("Force close Steam + games (stop timer)", null, async (_, _) => await ForceCloseAsync());
        _borrowMenuItem = new ToolStripMenuItem(Limits.BorrowButtonText(0));
        _borrowMenuItem.Click += async (_, _) => await BorrowAsync("borrow", "Borrow");
        menu.Items.Add(_borrowMenuItem);
        _dotaBorrowMenuItem = new ToolStripMenuItem(Limits.DotaBorrowButtonText(0));
        _dotaBorrowMenuItem.Click += async (_, _) => await BorrowAsync("borrowdota", "Dota borrow");
        menu.Items.Add(_dotaBorrowMenuItem);
        menu.Items.Add("Exit tray (timer keeps running, popups stop)", null, (_, _) =>
        {
            if (!_gate.TryEnter()) return;
            System.Windows.Forms.DialogResult answer;
            try
            {
                answer = System.Windows.Forms.MessageBox.Show(_getWindow()?.DialogOwner,
                    "Exit the tray? The timer keeps running, but you will NOT get the 15-minute / time-up popups.",
                    "Steam Timer", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Warning);
            }
            finally { _gate.Exit(); }
            if (answer == System.Windows.Forms.DialogResult.Yes)
            {
                _icon.Visible = false;
                ((SteamTimerApp)Microsoft.UI.Xaml.Application.Current).Shutdown();
            }
        });
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => _showMain();
        _gate.Changed += () => { _borrowMenuItem.Enabled = _dotaBorrowMenuItem.Enabled = !_gate.Busy; };
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();
        _ = PollAsync();
    }

    // The only place the tooltip and icon change. Unchanged values are skipped
    // so the shell is not updated every second.
    internal void Render((string Text, TrayLevel Level) status)
    {
        if (_disposed) return;
        string text = status.Text.Length > 63 ? status.Text[..63] : status.Text;
        if (text != _shownText)
        {
            try { _icon.Text = text; } catch { }
            _shownText = text;
        }
        if (status.Level != _shownLevel)
        {
            _icon.Icon = status.Level switch
            {
                TrayLevel.Blocked => SystemIcons.Error,
                TrayLevel.Low => SystemIcons.Warning,
                _ => SystemIcons.Shield,
            };
            _shownLevel = status.Level;
        }
    }

    public void Balloon(string title, string body)
    {
        try { _icon.ShowBalloonTip(8000, title, body, ToolTipIcon.Warning); } catch { }
    }

    private async Task PollAsync()
    {
        if (_polling || _disposed) return;
        _polling = true;
        try
        {
            var reply = await PipeClient.SendAsync(new PipeRequest { Cmd = "status" });
            if (_disposed) return;
            if (reply.Response is not { Ok: true } resp)
            {
                _getWindow()?.MarkServiceUnavailable(reply.Response?.Error is { Length: > 0 } error ? error : reply.Problem);
                return;
            }
            try { _getWindow()?.UpdateFromStatus(resp); } catch { }
            UpdateMenu(resp);
            Notify(resp);
        }
        catch { }
        finally { _polling = false; }
    }

    private void UpdateMenu(PipeResponse resp)
    {
        try { _borrowMenuItem.Text = Limits.BorrowButtonText(resp.BorrowCount); } catch { }
        try { _dotaBorrowMenuItem.Text = Limits.DotaBorrowButtonText(resp.DotaBorrowCount); } catch { }
    }

    private void Notify(PipeResponse resp)
    {
        var serverNow = PolicyClock.FromUtc(resp.ServerNowUtc);
        if (resp.LogicalDay != _seenDay)
        {
            _seenDay = resp.LogicalDay;
            _seenSessionKey = null; _sessionWarned = false;
            _seenCooldownKey = null; _cooldownTold = false;
        }
        string budgetKey = resp.LogicalDay + "|" + resp.BudgetSeconds;
        if (budgetKey != _seenBudgetKey) { _seenBudgetKey = budgetKey; _warned = false; _enforceTold = false; }
        string dotaKey = resp.DotaWeekStart + "|" + resp.DotaBudgetSeconds;
        if (dotaKey != _seenDotaKey) { _seenDotaKey = dotaKey; _dotaWarned = false; _dotaTold = false; }
        if (resp.LogicalDay != _seenForbiddenDay) { _seenForbiddenDay = resp.LogicalDay; _forbiddenTold = false; }

        if (resp.OutsidePlayHours)
        {
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
            if (!_enforceTold) { _enforceTold = true; Balloon("Steam time is up", $"Daily budget used. Blocked until {Limits.ResetClockText}."); }
            _wasCooldown = false;
            return;
        }
        if (resp.InCooldown)
        {
            var key = resp.LogicalDay + "|cd|" + resp.CooldownUntilUtc.Ticks;
            if (key != _seenCooldownKey) { _seenCooldownKey = key; _cooldownTold = false; }
            if (!_cooldownTold)
            {
                _cooldownTold = true;
                string until = resp.CooldownUntilUtc == default ? "" : $" Next session after {PolicyClock.FromUtc(resp.CooldownUntilUtc):HH:mm}.";
                Balloon("Cooldown — Steam blocked", $"{TimeText.Hm(resp.CooldownRemainingSeconds)} until the next session.{until}");
            }
            _wasCooldown = true;
            return;
        }
        if (_wasCooldown) { _wasCooldown = false; Balloon("Cooldown over", "You can play again."); }

        bool sessionEnding = resp.Running && resp.SessionRemainingSeconds < resp.RemainingSeconds &&
            resp.SessionRemainingSeconds <= Limits.WarnBefore.TotalSeconds && resp.SessionRemainingSeconds > 0;
        bool dotaLow = resp.DotaRunning && !resp.DotaForbidden && !resp.DotaEnforcing &&
            resp.DotaRemainingSeconds <= Limits.WarnBefore.TotalSeconds && resp.DotaRemainingSeconds > 0;
        if (resp.Running && resp.RemainingSeconds <= Limits.WarnBefore.TotalSeconds && !_warned)
        {
            _warned = true;
            Balloon("Steam closes in 15 minutes", $"{TimeText.Hm(resp.RemainingSeconds)} of today's budget left. Save your game.");
        }
        if (sessionEnding)
        {
            var key = resp.LogicalDay + "|" + resp.StartUtc.Ticks;
            if (key != _seenSessionKey) { _seenSessionKey = key; _sessionWarned = false; }
            if (!_sessionWarned) { _sessionWarned = true; Balloon("Session ending", $"This session ends in {TimeText.Hm(resp.SessionRemainingSeconds)}, then a cooldown. Save your game."); }
        }
        else { _seenSessionKey = null; _sessionWarned = false; }
        // Weekday play window: warn when it is the first limit to end play.
        if (resp.Running && resp.WindowEndsLocal is { } windowEnd)
        {
            double windowLeft = (windowEnd - serverNow).TotalSeconds;
            string key = windowEnd.ToString("yyyy-MM-dd HH:mm");
            if (key != _windowWarnKey && windowLeft > 0 && windowLeft <= Limits.WarnBefore.TotalSeconds
                && windowLeft < resp.RemainingSeconds && windowLeft < resp.SessionRemainingSeconds)
            {
                _windowWarnKey = key;
                Balloon("Play window closes soon",
                    $"Steam closes at {windowEnd:HH:mm} when play hours end ({Limits.FormatHm(TimeSpan.FromSeconds(windowLeft))} left). Save your game.");
            }
        }
        if (resp.DotaForbidden)
        {
            if (!_forbiddenTold) { _forbiddenTold = true; Balloon("Dota 2 forbidden on Mon / Wed / Fri", "No Dota today — back tomorrow. Steam + other games continue."); }
        }
        else if (resp.DotaEnforcing)
        {
            if (!_dotaTold)
            {
                _dotaTold = true;
                if (resp.WorkoutGatesToday && resp.DotaBudgetSeconds <= 0)
                    Balloon("Dota 2 needs a workout", "No workout credit this week yet, so weekday Dota is blocked. Other games continue.");
                else
                    Balloon("Dota 2 time is up", "This week's Dota allowance is used. Other games continue.");
            }
        }
        else if (dotaLow && !_dotaWarned)
        {
            _dotaWarned = true;
            Balloon("Dota 2 closes in 15 minutes", $"{TimeText.Hm(resp.DotaRemainingSeconds)} of the weekly Dota budget left. Other games are unaffected.");
        }
    }

    private async Task RecheckAsync()
    {
        if (!_gate.TryEnter()) return;
        try
        {
            var reply = await PipeClient.SendAsync(new PipeRequest { Cmd = "recheck" });
            if (reply.Response is not { Ok: true } resp) { Balloon("Recheck failed", reply.Response?.Error is { Length: > 0 } e ? e : reply.Problem); return; }
            try { _getWindow()?.UpdateFromStatus(resp); } catch { }
            Balloon("Recheck", string.IsNullOrWhiteSpace(resp.ActionNote) ? "Done." : resp.ActionNote);
        }
        finally { _gate.Exit(); }
    }

    private async Task ForceCloseAsync()
    {
        if (!_gate.TryEnter()) return;
        try
        {
            var confirm = System.Windows.Forms.MessageBox.Show(_getWindow()?.DialogOwner,
                "Force-close Steam + detected games now? This stops the timer immediately (the normal cooldown for the time played still applies).",
                "Steam Timer — force close", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Warning);
            if (confirm != System.Windows.Forms.DialogResult.Yes) return;
            var reply = await PipeClient.SendAsync(new PipeRequest { Cmd = "kill" }, timeoutMs: 15000);
            if (reply.Response is not { Ok: true } resp) { Balloon("Force-close failed", reply.Response?.Error is { Length: > 0 } e ? e : reply.Problem); return; }
            try { _getWindow()?.UpdateFromStatus(resp); } catch { }
            Balloon("Force close", string.IsNullOrWhiteSpace(resp.ActionNote) ? $"Closed {resp.KilledProcesses} process(es)." : resp.ActionNote);
        }
        finally { _gate.Exit(); }
    }

    private async Task BorrowAsync(string cmd, string label)
    {
        if (!_gate.TryEnter()) return;
        try
        {
            var reply = await PipeClient.SendAsync(new PipeRequest { Cmd = cmd }, timeoutMs: 15000);
            if (reply.Response is not { } resp) { Balloon($"{label} failed", reply.Problem); return; }
            try { _getWindow()?.UpdateFromStatus(resp); } catch { }
            if (!resp.Ok) { Balloon($"{label} unavailable", resp.Error); return; }
            UpdateMenu(resp);
        }
        finally { _gate.Exit(); }
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
