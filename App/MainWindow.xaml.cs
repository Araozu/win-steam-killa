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

    public void SetTray(TrayIcon tray)
    {
        _tray = tray;
        RefreshCountdown();
    }

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

    internal bool ActionInProgress => _busy;

    internal async Task<(PipeResponse? Response, bool Applied)> SendToServiceAsync(string command, int timeoutMs = 3000)
    {
        long request = _state.BeginRequest();
        var response = await PipeClient.SendAsync(new PipeRequest { Cmd = command }, timeoutMs);
        // A rejected borrow still carries current status. Empty/error-only replies
        // do not replace the last usable snapshot.
        var snapshot = response is { Ok: true } or { SessionMaxSeconds: > 0 } ? response : null;
        bool applied = !_allowClose && _state.CompleteRequest(request, snapshot);
        if (applied) RefreshCountdown();
        return (response, applied);
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
        const string waiting = "Waiting for current service status.";
        bool current = _state.IsCurrent;
        double tomorrow = TomorrowRemaining(r);
        double nextWeek = NextWeekDotaRemaining(r);
        double cost = Limits.BorrowCost.TotalSeconds;
        bool sunday = LogicalDay.TryParse(r.LogicalDay, out var day) && day.DayOfWeek == DayOfWeek.Sunday;

        TomorrowText.Text = $"Tomorrow: {Hm(tomorrow)}";
        NextWeekText.Text = $"Next week: {Hm(nextWeek)}";
        double eff = r.NextBorrowEffectiveness > 0 ? r.NextBorrowEffectiveness : Limits.NextBorrowEffectiveness(r.BorrowCount);
        double deff = r.NextDotaBorrowEffectiveness > 0 ? r.NextDotaBorrowEffectiveness : Limits.NextDotaBorrowEffectiveness(r.DotaBorrowCount);
        BorrowDayBtn.Background = HeatBrush(eff, Limits.BorrowMinEffectiveness);
        BorrowDotaBtn.Background = HeatBrush(deff, Limits.DotaBorrowMinEffectiveness);

        SetBorrowOffer(false, Limits.BorrowButtonText(r.BorrowCount), current && !_busy && !sunday && tomorrow >= cost,
            !current ? waiting : sunday ? "Borrow is unavailable on Sundays."
            : tomorrow < cost ? "Tomorrow has less than 10m left to borrow." : "Cooldowns still apply.");
        SetBorrowOffer(true, Limits.DotaBorrowButtonText(r.DotaBorrowCount), current && !_busy && nextWeek >= cost,
            !current ? waiting : nextWeek < cost ? "Next week has less than 10m of Dota left to borrow." : "");
    }

    private void SetBorrowOffer(bool dota, string label, bool enabled, string tooltip)
    {
        var button = dota ? BorrowDotaBtn : BorrowDayBtn;
        button.Content = label;
        button.IsEnabled = enabled;
        button.ToolTip = string.IsNullOrEmpty(tooltip) ? null : tooltip;
        _tray?.SetBorrowOffer(dota, label, enabled, tooltip);
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
            await SendToServiceAsync("status");
        }
        finally { _syncing = false; }
    }

    private void RefreshCountdown()
    {
        if (_allowClose) return;
        ActionText.Text = _lastNote ?? "";
        var values = _state.Last == null ? default : _state.Project(DateTime.Now);
        StatusText.Text = !_state.ServiceAvailable
            ? "Service unreachable — is SteamTimer running? Values are last known."
            : _state.Last == null ? "Waiting for the service…"
            : !_state.IsCurrent ? "Service status is stale — values are last known."
            : _state.Last.OutsidePlayHours ? $"Outside play hours — back {_state.Last.NextPlayAllowedLocal:HH:mm}"
            : _state.Last.Enforcing || _state.Last.RemainingSeconds <= 0 ? "Daily budget used up — blocked until 03:00"
            : _state.Last.InCooldown ? values.CooldownRemaining > 0
                ? "Cooldown — Steam blocked between sessions" : "Checking cooldown with the service…"
            : _state.Last.Running ? "Playing — timer running" : "Ready — Steam timer paused";
        RecheckBtn.IsEnabled = ForceBtn.IsEnabled = ResetBtn.IsEnabled = !_busy;
        if (_state.Last == null)
        {
            RemH.Text = "–"; RemM.Text = "–"; RemS.Text = "–";
            _todFrac = 0; _sessFrac = 0; _dayFrac = 0; _dotaFrac = 0;
            ApplyFills();
            DetectorText.Text = "Detector: —";
            const string waiting = "Waiting for current service status.";
            SetBorrowOffer(false, "Borrow from tomorrow", false, waiting);
            SetBorrowOffer(true, "Borrow from next week", false, waiting);
            if (!_state.ServiceAvailable)
                _tray?.SetState("Service unreachable — is SteamTimer running?", null);
            return;
        }
        var r = _state.Last;
        RefreshBorrowButtons(r);

        double elapsed = _state.ElapsedSeconds;
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
            SessMid.Text = cd <= 0 ? "checking cooldown…" : $"cooldown {FormatCooldown(cd)}";
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

        if (_state.IsCurrent) _tray?.SetStateFromWindow(r, remaining, values.CooldownRemaining);
        else _tray?.SetState(_state.ServiceAvailable ? "Service status is stale — last known values" : "Service unreachable — is SteamTimer running?", null);
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e) => await RecheckAsync();
    private async void Force_Click(object sender, RoutedEventArgs e) => await ForceCloseAsync();
    private async void BorrowDay_Click(object sender, RoutedEventArgs e) => await BorrowAsync(dota: false);
    private async void BorrowDota_Click(object sender, RoutedEventArgs e) => await BorrowAsync(dota: true);

    internal Task RecheckAsync(bool notify = false) => RunActionAsync("recheck", "Recheck", 3000, notify);

    internal async Task ForceCloseAsync(bool notify = false)
    {
        if (_busy) return;
        string prompt = "Force-close Steam and detected games now? The normal cooldown for time played still applies.";
        if (_state.IsCurrent && _state.Last is { SteamRunning: true } last)
            prompt = $"The service currently sees: {DescribeSeen(last)}.\n\n{prompt}";
        const string title = "Steam Timer — force close";
        var confirm = notify
            ? System.Windows.MessageBox.Show(prompt, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : System.Windows.MessageBox.Show(this, prompt, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes)
            await RunActionAsync("kill", "Force close", 15000, notify);
    }

    internal Task BorrowAsync(bool dota, bool notify = false)
    {
        if (!_state.IsCurrent || !(dota ? BorrowDotaBtn : BorrowDayBtn).IsEnabled)
            return Task.CompletedTask;
        return RunActionAsync(dota ? "borrowdota" : "borrow", dota ? "Dota borrow" : "Borrow", 15000, notify);
    }

    private async Task RunActionAsync(string command, string title, int timeoutMs, bool notify)
    {
        if (_busy || _allowClose) return;
        _busy = true;
        _lastNote = null;
        RefreshCountdown();
        try
        {
            var (response, _) = await SendToServiceAsync(command, timeoutMs);
            _lastNote = response switch
            {
                null => $"{title} failed — service unreachable.",
                { Ok: false } => response.Error,
                _ => response.ActionNote,
            };
            if (notify && !_allowClose) _tray?.Balloon(title, _lastNote);
        }
        finally
        {
            _busy = false;
            RefreshCountdown();
        }
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
