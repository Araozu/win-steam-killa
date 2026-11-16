using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Shared;

namespace App;

public partial class MainWindow : Window
{
    private TrayIcon? _tray;
    private readonly TimerViewState _state = new();
    private string? _lastNote;
    private bool _busy;
    private bool _syncing;
    private bool _allowClose;
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private double _todFrac, _sessFrac, _dayFrac, _dotaFrac;

    public MainWindow()
    {
        InitializeComponent();
        RefreshCountdown();
        _uiTimer.Tick += (_, _) => RefreshCountdown();
        _uiTimer.Start();
        TodTrack.SizeChanged += (_, _) => ApplyFills();
        SessTrack.SizeChanged += (_, _) => ApplyFills();
        DayTrack.SizeChanged += (_, _) => ApplyFills();
        DotaTrack.SizeChanged += (_, _) => ApplyFills();
        Loaded += async (_, _) => await SyncFromServiceAsync();
    }

    public void SetTray(TrayIcon tray) => _tray = tray;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var workArea = transform.Transform(new System.Windows.Vector(screen.WorkingArea.Width, screen.WorkingArea.Height));
        MinWidth = Math.Min(MinWidth, workArea.X);
        MinHeight = Math.Min(MinHeight, workArea.Y);
        Width = Math.Min(Width, workArea.X);
        Height = Math.Min(Height, workArea.Y);
    }

    public void ShowMain()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void AllowCloseAndDispose()
    {
        _allowClose = true;
        _uiTimer.Stop();
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    public void UpdateFromStatus(PipeResponse resp, bool keepNote = false)
    {
        if (_allowClose || !_state.Update(resp)) return;
        if (!keepNote) _lastNote = string.IsNullOrWhiteSpace(resp.ActionNote) ? _lastNote : resp.ActionNote;
        RefreshCountdown();
    }

    public void MarkServiceUnavailable()
    {
        if (_allowClose) return;
        _state.MarkUnavailable();
        RefreshCountdown();
    }

    private static string Hm(double totalSeconds)
    {
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds)) return "—";
        if (totalSeconds < 0) totalSeconds = 0;
        if (totalSeconds > TimeSpan.MaxValue.TotalSeconds) totalSeconds = TimeSpan.MaxValue.TotalSeconds;
        var t = TimeSpan.FromSeconds(totalSeconds);
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }

    private static string FmtMax(double totalSeconds)
    {
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds)) return "—";
        totalSeconds = Math.Max(0, totalSeconds);
        int mins = (int)Math.Round(totalSeconds / 60);
        int h = mins / 60, m = mins % 60;
        return m == 0 ? $"{h}h" : $"{h}h {m:00}m";
    }

    private static string FormatCooldown(double totalSeconds)
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
        return Hm(totalSeconds);
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
                ? day.AddDays(1).DayOfWeek : DateTime.Now.AddDays(1).DayOfWeek).TotalSeconds;
        return Math.Max(0, tomorrowBase - r.BorrowCount * Limits.BorrowCost.TotalSeconds);
    }

    private static double NextWeekDotaRemaining(PipeResponse r)
    {
        double debt = r.DotaBorrowCount * Limits.BorrowCost.TotalSeconds;
        return Math.Max(0, Limits.DotaWeeklyBudget.TotalSeconds - debt);
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
        double eff = r.NextBorrowEffectiveness > 0 ? r.NextBorrowEffectiveness : Limits.NextBorrowEffectiveness(r.BorrowCount);
        BorrowDayBtn.Content = $"Borrow 10m from tomorrow ({Limits.FormatEffectiveness(eff)})";
        BorrowDayBtn.Background = HeatBrush(eff, Limits.BorrowMinEffectiveness);
        TomorrowText.Text = $"Tomorrow: {Hm(TomorrowRemaining(r))}";
        bool sunday = LogicalDay.TryParse(r.LogicalDay, out var day) && day.DayOfWeek == DayOfWeek.Sunday;
        BorrowDayBtn.IsEnabled = !_busy && _state.IsCurrent && !sunday && TomorrowRemaining(r) >= Limits.BorrowCost.TotalSeconds;
        BorrowDayBtn.ToolTip = sunday ? "Borrow is unavailable on Sundays."
            : TomorrowRemaining(r) < Limits.BorrowCost.TotalSeconds ? "Tomorrow has less than 10m left to borrow."
            : $"Spend 10m of tomorrow for +{Limits.FormatGain(Limits.BorrowGainForNumber(r.BorrowCount + 1))} today. Cooldowns still apply.";
        double deff = r.NextDotaBorrowEffectiveness > 0 ? r.NextDotaBorrowEffectiveness : Limits.NextDotaBorrowEffectiveness(r.DotaBorrowCount);
        BorrowDotaBtn.Content = $"Borrow 10m from next week ({Limits.FormatEffectiveness(deff)})";
        BorrowDotaBtn.Background = HeatBrush(deff, Limits.DotaBorrowMinEffectiveness);
        NextWeekText.Text = $"Next week: {Hm(NextWeekDotaRemaining(r))}";
        BorrowDotaBtn.IsEnabled = !_busy && _state.IsCurrent && NextWeekDotaRemaining(r) >= Limits.BorrowCost.TotalSeconds;
        BorrowDotaBtn.ToolTip = NextWeekDotaRemaining(r) < Limits.BorrowCost.TotalSeconds ? "Next week has less than 10m of Dota left to borrow."
            : $"Spend 10m of next week's Dota budget for +{Limits.FormatGain(Limits.DotaBorrowGainForNumber(r.DotaBorrowCount + 1))} this week.";
    }

    private static System.Windows.Media.Brush HeatBrush(double eff, double floor)
    {
        double t = 1 - (eff - floor) / (Limits.BorrowFirstEffectiveness - floor);
        t = Math.Clamp(t, 0, 1);
        byte r = (byte)(255 - 5 * t);
        byte g = (byte)(255 - 55 * t);
        byte b = (byte)(255 - 55 * t);
        return new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
    }

    private async Task SyncFromServiceAsync()
    {
        if (_syncing || _allowClose) return;
        _syncing = true;
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "status" });
            if (resp == null || !resp.Ok) { MarkServiceUnavailable(); return; }
            UpdateFromStatus(resp);
        }
        finally { _syncing = false; }
    }

    private void RefreshCountdown()
    {
        if (_allowClose) return;
        ActionText.Text = _lastNote ?? "";
        StatusText.Text = !_state.ServiceAvailable
            ? "Service unreachable — is SteamTimer running? Values are last known."
            : _state.Last == null ? "Waiting for the service…"
            : !_state.IsCurrent ? "Service status is stale — values are last known."
            : _state.Last.OutsidePlayHours ? $"Outside play hours — back {_state.Last.NextPlayAllowedLocal:HH:mm}"
            : _state.Last.Enforcing || _state.Last.RemainingSeconds <= 0 ? "Daily budget used up — blocked until 03:00"
            : _state.Last.InCooldown ? "Cooldown — Steam blocked between sessions"
            : _state.Last.Running ? "Playing — timer running" : "Ready — Steam timer paused";
        RecheckBtn.IsEnabled = ForceBtn.IsEnabled = ResetBtn.IsEnabled = !_busy;
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
        var values = _state.Project(DateTime.Now);
        double remaining = values.Remaining, sessElapsed = values.SessionElapsed, dotaRem = values.DotaRemaining;
        double sessionMax = values.SessionMax;
        double dailyMax = Math.Max(0, r.BudgetSeconds);
        double dotaMax = Math.Max(0, r.DotaBudgetSeconds);

        // ---- Remaining: most restrictive ----
        int totalSec = (int)Math.Ceiling(values.Playable);
        RemH.Text = _state.IsCurrent ? (totalSec / 3600).ToString("00") : "–";
        RemM.Text = _state.IsCurrent ? ((totalSec % 3600) / 60).ToString("00") : "–";
        RemS.Text = _state.IsCurrent ? (totalSec % 60).ToString("00") : "–";

        // ---- Time of day: fills as window elapses ----
        var now = DateTime.Now;
        if (Limits.IsWeekend(now.DayOfWeek))
        {
            TodStart.Text = "—"; TodNow.Text = now.ToString("HH:mm"); TodEnd.Text = "free";
            _todFrac = 0;
        }
        else
        {
            var t = now.TimeOfDay;
            bool in1 = t >= Limits.WeekdayWindow1Start && t < Limits.WeekdayWindow1End;
            bool in2 = t >= Limits.WeekdayWindow2Start && t < Limits.WeekdayWindow2End;
            if (in1 || in2)
            {
                var s = in1 ? Limits.WeekdayWindow1Start : Limits.WeekdayWindow2Start;
                var e = in1 ? Limits.WeekdayWindow1End : Limits.WeekdayWindow2End;
                TodStart.Text = new DateTime().Add(s).ToString("HH:mm");
                TodEnd.Text = new DateTime().Add(e).ToString("HH:mm");
                TodNow.Text = now.ToString("HH:mm");
                _todFrac = (t - s).TotalSeconds / (e - s).TotalSeconds;
            }
            else
            {
                TodStart.Text = "—"; TodNow.Text = now.ToString("HH:mm"); TodEnd.Text = r.NextPlayAllowedLocal.ToString("HH:mm");
                _todFrac = 1;
            }
        }

        // ---- Session: fills with played ----
        bool cooling = !r.Enforcing && r.InCooldown && !r.OutsidePlayHours;
        if (r.OutsidePlayHours)
        {
            SessFill.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x2F, 0x20));
            _sessFrac = 1;
            SessMid.Text = $"back {r.NextPlayAllowedLocal:HH:mm}";
            SessMax.Text = FmtMax(sessionMax);
        }
        else if (r.Enforcing || remaining <= 0)
        {
            SessFill.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x2F, 0x20));
            _sessFrac = 1;
            SessMid.Text = "blocked";
            SessMax.Text = FmtMax(sessionMax);
        }
        else if (cooling)
        {
            double cd = values.CooldownRemaining;
            double total = r.CooldownTotalSeconds > 0 ? r.CooldownTotalSeconds : Limits.CooldownForSession(TimeSpan.FromSeconds(r.LastSessionSeconds)).TotalSeconds;
            if (total <= 0) total = Math.Max(cd, 1);
            SessFill.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x2F, 0x20));
            _sessFrac = total > 0 ? cd / total : 0;
            SessMid.Text = cd <= 0 ? "ready" : $"cooldown {FormatCooldown(cd)}";
            SessMax.Text = Hm(total);
        }
        else if (r.Running)
        {
            SessFill.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x04, 0x64));
            _sessFrac = sessionMax > 0 ? sessElapsed / sessionMax : 0;
            SessMid.Text = $"{FormatCooldown(sessElapsed)} played";
            SessMax.Text = FmtMax(sessionMax);
        }
        else
        {
            SessFill.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x04, 0x64));
            _sessFrac = 0;
            SessMid.Text = "ready";
            SessMax.Text = FmtMax(sessionMax);
        }

        // ---- Daily allowance: depletes ----
        _dayFrac = dailyMax > 0 ? remaining / dailyMax : 0;
        DayMid.Text = Hm(remaining);
        DayMax.Text = FmtMax(dailyMax);

        // ---- Dota allowance: depletes ----
        _dotaFrac = dotaMax > 0 ? (r.DotaForbidden ? 0 : dotaRem) / dotaMax : 0;
        DotaMid.Text = r.DotaForbidden ? "forbidden" : r.DotaEnforcing && r.WorkoutGatesToday && r.WorkoutDotaEarnedSeconds <= 0
            ? "needs workout" : Hm(dotaRem);
        DotaMax.Text = FmtMax(dotaMax);

        ApplyFills();

        DetectorText.Text = r.SteamRunning ? "Detector: play detected" : "Detector: nothing detected";

        string hoursLine = r.OutsidePlayHours
            ? $"\nPlay hours {Limits.PlayHoursSummary} (weekends free).\nBlocked until {r.NextPlayAllowedLocal:HH:mm}."
            : r.WindowEndsLocal != null
            ? $"\nPlay hours {Limits.PlayHoursSummary} (weekends free).\nPlayable until {r.WindowEndsLocal:HH:mm}."
            : $"\nPlay hours {Limits.PlayHoursSummary} (weekends free).";
        TodayDetail.Text = $"Budget {Hm(dailyMax)} total\nBase {Hm(r.BaseBudgetSeconds)} + carry {Hm(r.CarryInSeconds)}" +
            (r.BorrowedSeconds > 0 ? $" + borrow {Hm(r.BorrowedSeconds)}" : "") +
            (r.AdvanceDeductionSeconds > 0 ? $" − advance {Hm(r.AdvanceDeductionSeconds)}" : "") +
            $"\nUsed {Hm(r.UsedSeconds)}, left {Hm(remaining)}" +
            $"\n\nDota {Hm(dotaMax)} total\n" + (r.WorkoutGatesToday
                ? $"Earned {Hm(r.WorkoutDotaEarnedSeconds)} via workout (8h cap)"
                : $"Base {Hm(r.DotaBaseBudgetSeconds > 0 ? r.DotaBaseBudgetSeconds : Limits.DotaWeeklyBudget.TotalSeconds)} (weekends free)") +
            (r.DotaBorrowedSeconds > 0 ? $" + borrow {Hm(r.DotaBorrowedSeconds)}" : "") +
            (r.DotaAdvanceDeductionSeconds > 0 ? $" − advance {Hm(r.DotaAdvanceDeductionSeconds)}" : "") +
            $"\nUsed {Hm(r.DotaUsedSeconds)}, left {Hm(dotaRem)}" +
            (r.DotaForbidden ? "\nNo Dota on Mon / Wed / Fri." : "") + hoursLine;
        DetectorDetail.Text = "Sees now: " + DescribeSeen(r) + ".\n" +
            (r.RememberedGames.Count > 0 ? "Remembered: " + string.Join(", ", r.RememberedGames) + "." : "Remembered: none.") +
            $"\nLast poll {Math.Max(0, (int)elapsed)}s ago.";

        if (_state.IsCurrent) _tray?.SetStateFromWindow(r, remaining, elapsed);
        else _tray?.SetState(_state.ServiceAvailable ? "Service status is stale — last known values" : "Service unreachable — is SteamTimer running?", null);
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; RefreshCountdown();
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "recheck" });
            if (resp == null || !resp.Ok) { _lastNote = "Recheck failed — service unreachable."; MarkServiceUnavailable(); return; }
            _lastNote = resp.ActionNote;
            UpdateFromStatus(resp, keepNote: true);
        }
        finally { _busy = false; RefreshCountdown(); }
    }

    private async void Force_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var last = _state.Last;
        string what = last != null && (last.LiveClients.Count > 0 || last.LiveGames.Count > 0 || last.MatchedOrphans.Count > 0)
            ? $"The service currently sees: {DescribeSeen(last)}.\n\n" : "";
        if (System.Windows.MessageBox.Show(this, $"{what}Force-close Steam + detected games now? This stops the timer immediately (the normal cooldown for the time played still applies).",
            "Steam Timer — force close", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true; RefreshCountdown();
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "kill" }, timeoutMs: 15000);
            if (resp == null || !resp.Ok) { _lastNote = "Force-close failed — service unreachable."; MarkServiceUnavailable(); return; }
            _lastNote = resp.ActionNote;
            UpdateFromStatus(resp, keepNote: true);
        }
        finally { _busy = false; RefreshCountdown(); }
    }

    private async void BorrowDay_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_state.Last is { } last && LogicalDay.TryParse(last.LogicalDay, out var ld) && ld.DayOfWeek == DayOfWeek.Sunday)
        {
            System.Windows.MessageBox.Show("Borrow is unavailable on Sundays — enjoy the 4h budget.", "Steam Timer — borrow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _busy = true; RefreshCountdown();
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "borrow" }, timeoutMs: 15000);
            if (resp == null) { _lastNote = "Borrow failed — service unreachable."; MarkServiceUnavailable(); return; }
            if (!resp.Ok) { _lastNote = resp.Error; UpdateFromStatus(resp, keepNote: true); return; }
            _lastNote = null;
            UpdateFromStatus(resp);
        }
        finally { _busy = false; RefreshCountdown(); }
    }

    private async void BorrowDota_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; RefreshCountdown();
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "borrowdota" }, timeoutMs: 15000);
            if (resp == null) { _lastNote = "Dota borrow failed — service unreachable."; MarkServiceUnavailable(); return; }
            if (!resp.Ok) { _lastNote = resp.Error; UpdateFromStatus(resp, keepNote: true); return; }
            _lastNote = null;
            UpdateFromStatus(resp);
        }
        finally { _busy = false; RefreshCountdown(); }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        _lastNote = "";
        RefreshCountdown();
        try { await SyncFromServiceAsync(); }
        finally { _busy = false; RefreshCountdown(); }
    }
}
