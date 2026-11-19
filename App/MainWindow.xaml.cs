using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Shared;
using Forms = System.Windows.Forms;

namespace App;

public partial class MainWindow : Window
{
    private TrayIcon? _tray;
    private readonly ActionGate _gate;
    private readonly TimerViewState _state = new();
    private readonly IntPtr _hwnd;
    private string? _lastNote;
    private bool _syncing;
    private bool _allowClose;
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private double _todFrac, _sessFrac, _dayFrac, _dotaFrac;

    internal MainWindow(ActionGate gate)
    {
        _gate = gate;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            WindowLayout.Background = null;
        }
        RefreshCountdown();
        // Runs while the window is hidden too: it keeps the tray tooltip live.
        _uiTimer.Tick += (_, _) => RefreshCountdown();
        _uiTimer.Start();
        _gate.Changed += RefreshCountdown;
        TodTrack.SizeChanged += (_, _) =>
        {
            UpdateClosedHoursStripes();
            ApplyFills();
        };
        SessTrack.SizeChanged += (_, _) => ApplyFills();
        DayTrack.SizeChanged += (_, _) => ApplyFills();
        DotaTrack.SizeChanged += (_, _) => ApplyFills();
        Root.Loaded += async (_, _) => await SyncFromServiceAsync();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(_hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min((int)(500 * dpi), area.Width), Math.Min((int)(600 * dpi), area.Height)));
        if (AppWindow.Presenter is OverlappedPresenter initialPresenter)
        {
            initialPresenter.PreferredMinimumWidth = Math.Min((int)(460 * dpi), area.Width);
            initialPresenter.PreferredMinimumHeight = Math.Min((int)(360 * dpi), area.Height);
        }
        AppWindow.Closing += (_, e) => { if (!_allowClose) { e.Cancel = true; AppWindow.Hide(); } };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // Owner for confirmation dialogs: modal to this window while it is shown.
    internal Forms.IWin32Window? DialogOwner => AppWindow.IsVisible ? new Win32Owner(_hwnd) : null;

    private sealed class Win32Owner(IntPtr handle) : Forms.IWin32Window
    {
        public IntPtr Handle => handle;
    }

    internal void SetTray(TrayIcon tray)
    {
        _tray = tray;
        RefreshCountdown();
    }

    public void ShowMain()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        Activate();
    }

    public void AllowCloseAndDispose()
    {
        _allowClose = true;
        _uiTimer.Stop();
        _gate.Changed -= RefreshCountdown;
        Close();
    }

    public void UpdateFromStatus(PipeResponse resp, bool keepNote = false)
    {
        if (_allowClose || !_state.Update(resp)) return;
        if (!keepNote) _lastNote = string.IsNullOrWhiteSpace(resp.ActionNote) ? _lastNote : resp.ActionNote;
        RefreshCountdown();
    }

    public void MarkServiceUnavailable(string? problem = null)
    {
        if (_allowClose) return;
        _state.MarkUnavailable(problem);
        RefreshCountdown();
    }

    private static string FmtMax(double totalSeconds)
    {
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds)) return "—";
        totalSeconds = Math.Max(0, totalSeconds);
        int mins = (int)Math.Round(totalSeconds / 60);
        int h = mins / 60, m = mins % 60;
        return m == 0 ? $"{h}h" : $"{h}h {m:00}m";
    }

    private static string FormatDuration(double totalSeconds)
    {
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds)) return "—";
        if (totalSeconds <= 0) return "0s";
        if (totalSeconds < 60) return $"{(int)Math.Ceiling(totalSeconds)}s";
        if (totalSeconds < 3600)
        {
            int m = (int)(totalSeconds / 60);
            int s = (int)Math.Ceiling(totalSeconds - m * 60);
            if (s == 60) { m++; s = 0; }
            return $"{m}m {s:00}s";
        }
        return TimeText.Hm(totalSeconds);
    }

    private static string DescribeSeen(PipeResponse r)
    {
        var bits = new List<string>();
        if (r.LiveClients.Count > 0) bits.Add("client: " + string.Join(", ", r.LiveClients));
        if (r.LiveGames.Count > 0) bits.Add("games: " + string.Join(", ", r.LiveGames));
        if (r.MatchedOrphans.Count > 0) bits.Add("orphans: " + string.Join(", ", r.MatchedOrphans));
        return bits.Count > 0 ? string.Join("; ", bits) : "nothing running";
    }

    private static double TomorrowRemaining(PipeResponse r)
    {
        double tomorrowBase = r.TomorrowBaseSeconds > 0
            ? r.TomorrowBaseSeconds
            : Limits.DailyBaseFor(LogicalDay.TryParse(r.LogicalDay, out var day)
                ? day.AddDays(1).DayOfWeek : PolicyClock.Now.AddDays(1).DayOfWeek).TotalSeconds;
        return Math.Max(0, tomorrowBase - r.BorrowCount * Limits.BorrowCost.TotalSeconds);
    }

    private static double NextWeekDotaRemaining(PipeResponse r)
    {
        double debt = r.DotaBorrowCount * Limits.BorrowCost.TotalSeconds;
        return Math.Max(0, Limits.DotaWeeklyBudget.TotalSeconds - debt);
    }

    private void UpdateClosedHoursStripes()
    {
        // An absolute diagonal gradient keeps the stripe spacing constant when resized.
        const double stripeStep = 8;
        double diagonal = TodTrack.ActualWidth + TodTrack.ActualHeight;
        if (diagonal <= 0) return;

        var stripes = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(diagonal / 2, diagonal / 2)
        };
        var red = Windows.UI.Color.FromArgb(255, 0xD9, 0x2F, 0x20);
        var lightRed = Windows.UI.Color.FromArgb(255, 0xFB, 0xAA, 0xA2);
        var color = red;
        stripes.GradientStops.Add(new GradientStop { Color = color, Offset = 0 });
        for (double position = stripeStep; position < diagonal; position += stripeStep)
        {
            double offset = position / diagonal;
            stripes.GradientStops.Add(new GradientStop { Color = color, Offset = offset });
            color = color == red ? lightRed : red;
            stripes.GradientStops.Add(new GradientStop { Color = color, Offset = offset });
        }
        stripes.GradientStops.Add(new GradientStop { Color = color, Offset = 1 });
        TodClosedTrack.Background = stripes;
    }

    private void ApplyFills()
    {
        SetFill(TodTrack, TodFill, _todFrac);
        SetFill(SessTrack, SessFill, _sessFrac);
        SetFill(DayTrack, DayFill, _dayFrac);
        SetFill(DotaTrack, DotaFill, _dotaFrac);
    }

    private static void SetFill(FrameworkElement track, FrameworkElement fill, double frac)
    {
        frac = Math.Clamp(frac, 0, 1);
        double w = track.ActualWidth;
        if (w > 0) fill.Width = Math.Max(0, w * frac);
    }

    private void RefreshBorrowButtons(PipeResponse r)
    {
        TomorrowText.Text = $"Tomorrow: {TimeText.Hm(TomorrowRemaining(r))}";
        bool sunday = LogicalDay.TryParse(r.LogicalDay, out var day) && day.DayOfWeek == DayOfWeek.Sunday;
        BorrowDayBtn.IsEnabled = !_gate.Busy && _state.IsCurrent && !sunday && TomorrowRemaining(r) >= Limits.BorrowCost.TotalSeconds;
        string dayReason = sunday ? "Borrow is unavailable on Sundays."
            : TomorrowRemaining(r) < Limits.BorrowCost.TotalSeconds ? "Tomorrow has less than 10m left to borrow."
            : $"Spend 10m of tomorrow for +{Limits.FormatGain(Limits.BorrowGainForNumber(r.BorrowCount + 1))} today. Cooldowns still apply.";
        ToolTipService.SetToolTip(BorrowDayBtn, dayReason);
        NextWeekText.Text = $"Next week: {TimeText.Hm(NextWeekDotaRemaining(r))}";
        BorrowDotaBtn.IsEnabled = !_gate.Busy && _state.IsCurrent && NextWeekDotaRemaining(r) >= Limits.BorrowCost.TotalSeconds;
        string dotaReason = NextWeekDotaRemaining(r) < Limits.BorrowCost.TotalSeconds ? "Next week has less than 10m of Dota left to borrow."
            : $"Spend 10m of next week's Dota budget for +{Limits.FormatGain(Limits.DotaBorrowGainForNumber(r.DotaBorrowCount + 1))} this week.";
        ToolTipService.SetToolTip(BorrowDotaBtn, dotaReason);
    }

    private async Task SyncFromServiceAsync()
    {
        if (_syncing || _allowClose) return;
        _syncing = true;
        try
        {
            var reply = await PipeClient.SendAsync(new PipeRequest { Cmd = "status" });
            if (reply.Response is not { Ok: true } resp) { MarkServiceUnavailable(reply.Response?.Error is { Length: > 0 } e ? e : reply.Problem); return; }
            UpdateFromStatus(resp);
        }
        finally { _syncing = false; }
    }

    private void RefreshCountdown()
    {
        if (_allowClose) return;
        var now = PolicyClock.Now;
        _tray?.Render(TrayStatus.Describe(_state, now));
        ActionText.Text = _lastNote ?? "";
        ActionText.Visibility = string.IsNullOrWhiteSpace(_lastNote) ? Visibility.Collapsed : Visibility.Visible;
        RemainingTime.Visibility = StatusText.Visibility = Visibility.Visible;
        UnavailableTime.Visibility = Visibility.Collapsed;
        StatusText.Text = !_state.ServiceAvailable
            ? $"{_state.Problem} Values are last known."
            : _state.Last == null ? "Waiting for the service…"
            : !_state.IsCurrent ? "Service status is stale — values are last known."
            : string.Empty;
        RecheckBtn.IsEnabled = ForceBtn.IsEnabled = ResetBtn.IsEnabled = !_gate.Busy;
        if (_state.Last == null)
        {
            RemH.Text = "–"; RemM.Text = "–"; RemS.Text = "–";
            _todFrac = 0; _sessFrac = 0; _dayFrac = 0; _dotaFrac = 0;
            ApplyFills();
            DetectorText.Text = "Detector: —";
            BorrowDayBtn.IsEnabled = BorrowDotaBtn.IsEnabled = false;
            return;
        }
        var r = _state.Last;
        RefreshBorrowButtons(r);

        double elapsed = _state.ElapsedSeconds;
        var values = _state.Project(now);
        double remaining = values.Remaining, sessElapsed = values.SessionElapsed, dotaRem = values.DotaRemaining;
        double sessionMax = values.SessionMax;
        double dailyMax = Math.Max(0, r.BudgetSeconds);
        double dotaMax = Math.Max(0, r.DotaBudgetSeconds);

        bool outsidePlayHours = !Limits.IsPlayAllowed(now);
        if (_state.IsCurrent)
        {
            bool blocked = outsidePlayHours || r.OutsidePlayHours || r.Enforcing || remaining <= 0;
            bool cooling = !blocked && r.InCooldown;
            blocked |= !cooling && values.Playable <= 0;
            if (blocked || cooling)
            {
                UnavailableTime.Text = blocked ? "Blocked" : "On cooldown";
                UnavailableTime.Visibility = Visibility.Visible;
                RemainingTime.Visibility = StatusText.Visibility = Visibility.Collapsed;
            }
            else
            {
                StatusText.Text = r.Running ? "Playing" : "Ready";
            }
        }

        // ---- Remaining: most restrictive ----
        int totalSec = outsidePlayHours ? 0 : (int)Math.Ceiling(values.Playable);
        RemH.Text = _state.IsCurrent ? (totalSec / 3600).ToString("00") : "–";
        RemM.Text = _state.IsCurrent ? ((totalSec % 3600) / 60).ToString("00") : "–";
        RemS.Text = _state.IsCurrent ? (totalSec % 60).ToString("00") : "–";

        // ---- Time of day: fills as window elapses ----
        TodClosedTrack.Visibility = outsidePlayHours ? Visibility.Visible : Visibility.Collapsed;
        TodNow.Text = Limits.FormatClock(now);
        if (Limits.CurrentWindowEndLocal(now) is { } windowEnd)
        {
            var windowStart = now.Date + (windowEnd.TimeOfDay == Limits.WeekdayWindow1End
                ? Limits.WeekdayWindow1Start : Limits.WeekdayWindow2Start);
            TodStart.Text = Limits.FormatClock(windowStart);
            TodEnd.Text = Limits.FormatClock(windowEnd);
            _todFrac = (now - windowStart).TotalSeconds / (windowEnd - windowStart).TotalSeconds;
        }
        else
        {
            TodStart.Text = "-";
            TodEnd.Text = outsidePlayHours ? Limits.FormatClock(Limits.NextPlayAllowedLocal(now)) : "free";
            _todFrac = 0;
        }

        // ---- Session: fills with played ----
        _sessFrac = sessionMax > 0 ? sessElapsed / sessionMax : 0;
        SessMid.Text = $"{FormatDuration(sessElapsed)} played";
        SessMax.Text = FmtMax(sessionMax);

        // ---- Daily allowance: depletes ----
        _dayFrac = dailyMax > 0 ? remaining / dailyMax : 0;
        DayMid.Text = TimeText.Hm(remaining);
        DayMax.Text = FmtMax(dailyMax);

        // ---- Dota allowance: depletes ----
        _dotaFrac = dotaMax > 0 ? (r.DotaForbidden ? 0 : dotaRem) / dotaMax : 0;
        DotaMid.Text = r.DotaForbidden ? "forbidden" : r.DotaEnforcing && r.WorkoutGatesToday && r.WorkoutDotaEarnedSeconds <= 0
            ? "needs workout" : TimeText.Hm(dotaRem);
        DotaMax.Text = FmtMax(dotaMax);

        ApplyFills();

        DetectorText.Text = r.SteamRunning ? "Detector: play detected" : "Detector: nothing detected";

        string hoursLine = $"\nPlay hours {Limits.PlayHoursSummary} {PolicyClock.Label} (weekends free; days start {Limits.ResetClockText})."
            + (r.OutsidePlayHours ? $"\nBlocked until {r.NextPlayAllowedLocal:HH:mm}."
                : r.WindowEndsLocal != null ? $"\nPlayable until {r.WindowEndsLocal:HH:mm}." : "");
        TodayDetail.Text = $"Budget {TimeText.Hm(dailyMax)} total\nBase {TimeText.Hm(r.BaseBudgetSeconds)} + carry {TimeText.Hm(r.CarryInSeconds)}" +
            (r.BorrowedSeconds > 0 ? $" + borrow {TimeText.Hm(r.BorrowedSeconds)}" : "") +
            (r.AdvanceDeductionSeconds > 0 ? $" − advance {TimeText.Hm(r.AdvanceDeductionSeconds)}" : "") +
            $"\nUsed {TimeText.Hm(r.UsedSeconds)}, left {TimeText.Hm(remaining)}" +
            $"\n\nDota {TimeText.Hm(dotaMax)} total\n" + (r.WorkoutGatesToday
                ? $"Earned {TimeText.Hm(r.WorkoutDotaEarnedSeconds)} via workout (8h cap)"
                : $"Base {TimeText.Hm(r.DotaBaseBudgetSeconds > 0 ? r.DotaBaseBudgetSeconds : Limits.DotaWeeklyBudget.TotalSeconds)} (weekends free)") +
            (r.DotaBorrowedSeconds > 0 ? $" + borrow {TimeText.Hm(r.DotaBorrowedSeconds)}" : "") +
            (r.DotaAdvanceDeductionSeconds > 0 ? $" − advance {TimeText.Hm(r.DotaAdvanceDeductionSeconds)}" : "") +
            $"\nUsed {TimeText.Hm(r.DotaUsedSeconds)}, left {TimeText.Hm(dotaRem)}" +
            (r.DotaForbidden ? "\nNo Dota on Mon / Wed / Fri." : "") + hoursLine;
        DetectorDetail.Text = "Sees now: " + DescribeSeen(r) + ".\n" +
            (r.RememberedGames.Count > 0 ? "Remembered: " + string.Join(", ", r.RememberedGames) + "." : "Remembered: none.") +
            $"\nLast poll {Math.Max(0, (int)elapsed)}s ago.";
    }

    // Sends one command and shows its note or error. Never throws.
    private async Task SendActionAsync(string cmd, int timeoutMs, string label)
    {
        var reply = await PipeClient.SendAsync(new PipeRequest { Cmd = cmd }, timeoutMs);
        if (reply.Response is not { } resp)
        {
            _lastNote = $"{label} failed — {reply.Problem}";
            MarkServiceUnavailable(reply.Problem);
            return;
        }
        _lastNote = resp.Ok ? resp.ActionNote : resp.Error;
        UpdateFromStatus(resp, keepNote: true);
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e)
    {
        if (!_gate.TryEnter()) return;
        try { await SendActionAsync("recheck", 3000, "Recheck"); }
        finally { _gate.Exit(); }
    }

    private async void Force_Click(object sender, RoutedEventArgs e)
    {
        if (!_gate.TryEnter()) return;
        try
        {
            var last = _state.Last;
            string what = last != null && (last.LiveClients.Count > 0 || last.LiveGames.Count > 0 || last.MatchedOrphans.Count > 0)
                ? $"The service currently sees: {DescribeSeen(last)}.\n\n" : "";
            if (Forms.MessageBox.Show(DialogOwner,
                    $"{what}Force-close Steam + detected games now? This stops the timer immediately (the normal cooldown for the time played still applies).",
                    "Steam Timer — force close", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Warning) != Forms.DialogResult.Yes)
                return;
            await SendActionAsync("kill", 15000, "Force-close");
        }
        finally { _gate.Exit(); }
    }

    private async void BorrowDay_Click(object sender, RoutedEventArgs e)
    {
        if (!_gate.TryEnter()) return;
        try
        {
            if (_state.Last is { } last && LogicalDay.TryParse(last.LogicalDay, out var ld) && ld.DayOfWeek == DayOfWeek.Sunday)
            {
                Forms.MessageBox.Show(DialogOwner, "Borrow is unavailable on Sundays — enjoy the 4h budget.", "Steam Timer — borrow",
                    Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
                return;
            }
            await SendActionAsync("borrow", 15000, "Borrow");
        }
        finally { _gate.Exit(); }
    }

    private async void BorrowDota_Click(object sender, RoutedEventArgs e)
    {
        if (!_gate.TryEnter()) return;
        try { await SendActionAsync("borrowdota", 15000, "Dota borrow"); }
        finally { _gate.Exit(); }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!_gate.TryEnter()) return;
        try
        {
            _lastNote = "";
            RefreshCountdown();
            await SyncFromServiceAsync();
        }
        finally { _gate.Exit(); }
    }
}
