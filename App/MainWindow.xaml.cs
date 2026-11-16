using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Shared;

namespace App;

public partial class MainWindow : Window
{
    private TrayIcon? _tray;
    private PipeResponse? _last;
    private DateTime _lastPollLocal = DateTime.Now;
    private string? _lastNote;
    private bool _serviceOk = true;
    private bool _busy;
    private bool _allowClose;
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private double _todFrac, _sessFrac = 0.45, _dayFrac = 0.2, _dotaFrac = 0.65;

    public MainWindow()
    {
        InitializeComponent();
        _uiTimer.Tick += (_, _) => RefreshCountdown();
        _uiTimer.Start();
        TodTrack.SizeChanged += (_, _) => ApplyFills();
        SessTrack.SizeChanged += (_, _) => ApplyFills();
        DayTrack.SizeChanged += (_, _) => ApplyFills();
        DotaTrack.SizeChanged += (_, _) => ApplyFills();
        Loaded += async (_, _) => await SyncFromServiceAsync();
    }

    public void SetTray(TrayIcon tray) => _tray = tray;

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
        if (_last != null && resp.ServerNowUtc < _last.ServerNowUtc) return;
        _last = resp;
        _lastPollLocal = DateTime.Now;
        _serviceOk = true;
        if (!keepNote) _lastNote = string.IsNullOrWhiteSpace(resp.ActionNote) ? _lastNote : resp.ActionNote;
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
            : Limits.DailyBaseFor(DateTime.Now.AddDays(1).DayOfWeek).TotalSeconds;
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
        BorrowDayBtn.Content = $"Borrow 10m ({Limits.FormatEffectiveness(eff)})";
        BorrowDayBtn.Background = HeatBrush(eff, Limits.BorrowMinEffectiveness);
        BorrowDotaBtn.Content = $"Borrow 10m ({Limits.FormatEffectiveness(r.NextDotaBorrowEffectiveness > 0 ? r.NextDotaBorrowEffectiveness : Limits.NextDotaBorrowEffectiveness(r.DotaBorrowCount))})";
        double deff = r.NextDotaBorrowEffectiveness > 0 ? r.NextDotaBorrowEffectiveness : Limits.NextDotaBorrowEffectiveness(r.DotaBorrowCount);
        BorrowDotaBtn.Background = HeatBrush(deff, Limits.DotaBorrowMinEffectiveness);
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
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "status" });
            if (resp == null || !resp.Ok) { _serviceOk = false; RefreshCountdown(); return; }
            _serviceOk = true;
            UpdateFromStatus(resp);
        }
        catch { _serviceOk = false; RefreshCountdown(); }
    }

    private void RefreshCountdown()
    {
        if (_last == null)
        {
            RemH.Text = "–"; RemM.Text = "–"; RemS.Text = "–";
            _todFrac = 0; _sessFrac = 0; _dayFrac = 0; _dotaFrac = 0;
            ApplyFills();
            DetectorText.Text = "Detector: —";
            ActionText.Text = _serviceOk ? "Waiting for the service…" : "Service unreachable — is SteamTimer running?";
            return;
        }
        var r = _last;
        RefreshBorrowButtons(r);

        double elapsed = Math.Max(0, (DateTime.Now - _lastPollLocal).TotalSeconds);
        double estimate = Math.Min(elapsed, 30);
        bool accruing = r.Running && !r.InCooldown && !r.OutsidePlayHours && !r.Enforcing;
        double remaining = r.RemainingSeconds;
        if (accruing && remaining > 0) remaining = Math.Max(0, remaining - estimate);
        double sessElapsed = r.SessionElapsedSeconds + (accruing ? estimate : 0);
        double dotaRem = r.DotaRemainingSeconds;
        if (accruing && r.DotaRunning && !r.DotaEnforcing && dotaRem > 0) dotaRem = Math.Max(0, dotaRem - estimate);

        double sessionMax = r.SessionMaxSeconds > 0 ? r.SessionMaxSeconds : Limits.SessionMax.TotalSeconds;
        double dailyMax = r.BudgetSeconds > 0 ? r.BudgetSeconds : 1;
        double dotaMax = r.DotaBudgetSeconds > 0 ? r.DotaBudgetSeconds : Limits.DotaWeeklyBudget.TotalSeconds;

        // ---- Remaining: most restrictive ----
        double windowLeft = r.WindowEndsLocal != null ? Math.Max(0, (r.WindowEndsLocal.Value - DateTime.Now).TotalSeconds) : double.PositiveInfinity;
        double sessionLeft = Math.Max(0, sessionMax - sessElapsed);
        double mostLeft = r.Enforcing ? 0 : Math.Min(Math.Min(sessionLeft, remaining), windowLeft);
        if (!r.Running && !r.InCooldown) mostLeft = Math.Min(remaining, double.IsPositiveInfinity(windowLeft) ? remaining : windowLeft);
        if (r.InCooldown || r.OutsidePlayHours) mostLeft = 0;
        if (double.IsPositiveInfinity(mostLeft) || double.IsNaN(mostLeft)) mostLeft = 0;
        int totalSec = (int)Math.Ceiling(mostLeft);
        RemH.Text = (totalSec / 3600).ToString("00");
        RemM.Text = ((totalSec % 3600) / 60).ToString("00");
        RemS.Text = (totalSec % 60).ToString("00");

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
            double cd = Math.Max(0, r.CooldownRemainingSeconds - elapsed);
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
        DotaMid.Text = r.DotaForbidden ? "forbidden" : Hm(dotaRem);
        DotaMax.Text = FmtMax(dotaMax);

        ApplyFills();

        DetectorText.Text = r.SteamRunning ? "Detector: Steam detected" : "Detector: nothing detected";
        ActionText.Text = _lastNote ?? "";

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
            $"\nUsed {Hm(r.DotaUsedSeconds)}, left {Hm(dotaRem)}" + hoursLine;
        DetectorDetail.Text = "Sees now: " + DescribeSeen(r) + ".\n" +
            (r.RememberedGames.Count > 0 ? "Remembered: " + string.Join(", ", r.RememberedGames) + "." : "Remembered: none.") +
            $"\nLast poll {Math.Max(0, (int)elapsed)}s ago.";

        _tray?.SetStateFromWindow(r, remaining, elapsed);
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; RecheckBtn.IsEnabled = false;
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "recheck" });
            if (resp == null || !resp.Ok) { _serviceOk = false; _lastNote = "Recheck failed — service unreachable."; RefreshCountdown(); return; }
            _serviceOk = true; _lastNote = resp.ActionNote;
            UpdateFromStatus(resp, keepNote: true);
        }
        finally { _busy = false; RecheckBtn.IsEnabled = true; }
    }

    private async void Force_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string what = _last != null && (_last.LiveClients.Count > 0 || _last.LiveGames.Count > 0 || _last.MatchedOrphans.Count > 0)
            ? $"The service currently sees: {DescribeSeen(_last)}.\n\n" : "";
        if (System.Windows.MessageBox.Show($"{what}Force-close Steam + detected games now? This stops the timer immediately (the normal cooldown for the time played still applies).",
            "Steam Timer — force close", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true; ForceBtn.IsEnabled = false;
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "kill" }, timeoutMs: 15000);
            if (resp == null || !resp.Ok) { _serviceOk = false; _lastNote = "Force-close failed — service unreachable."; RefreshCountdown(); return; }
            _serviceOk = true; _lastNote = resp.ActionNote;
            UpdateFromStatus(resp, keepNote: true);
        }
        finally { _busy = false; ForceBtn.IsEnabled = true; }
    }

    private async void BorrowDay_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_last != null && LogicalDay.TryParse(_last.LogicalDay, out var ld) && ld.DayOfWeek == DayOfWeek.Sunday)
        {
            System.Windows.MessageBox.Show("Borrow is unavailable on Sundays — enjoy the 4h budget.", "Steam Timer — borrow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _busy = true; BorrowDayBtn.IsEnabled = false;
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "borrow" }, timeoutMs: 15000);
            if (resp == null) { _serviceOk = false; _lastNote = "Borrow failed — service unreachable."; RefreshCountdown(); return; }
            if (!resp.Ok) { _lastNote = resp.Error; UpdateFromStatus(resp); _lastNote = resp.Error; RefreshCountdown(); return; }
            _serviceOk = true; _lastNote = null;
            UpdateFromStatus(resp);
        }
        finally { _busy = false; BorrowDayBtn.IsEnabled = true; }
    }

    private async void BorrowDota_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; BorrowDotaBtn.IsEnabled = false;
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "borrowdota" }, timeoutMs: 15000);
            if (resp == null) { _serviceOk = false; _lastNote = "Dota borrow failed — service unreachable."; RefreshCountdown(); return; }
            if (!resp.Ok) { _lastNote = resp.Error; UpdateFromStatus(resp); _lastNote = resp.Error; RefreshCountdown(); return; }
            _serviceOk = true; _lastNote = null;
            UpdateFromStatus(resp);
        }
        finally { _busy = false; BorrowDotaBtn.IsEnabled = true; }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        _lastNote = "";
        await SyncFromServiceAsync();
    }
}
