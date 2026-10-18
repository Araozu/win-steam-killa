using Shared;

namespace App;

// Timer controls: play time is auto-tracked by the service.
// Three manual actions exist for clarity / emergencies:
//   Recheck — ask the service what it sees RIGHT NOW (client/games/orphans).
//   Force close — kill Steam + games now and stop the timer (normal cooldown
//   for the time played still applies).
//   Borrow — take 10m off tomorrow for 95% today, then -7pp, -8pp,
//   -9pp, ... per further press (0.1% floor), one click, no confirm (repeatable,
//   no Sundays). The button heats toward red as effectiveness drops.
//
// Top of the window is three time bars:
//   Session (green, fills with played time toward the 2h max
//   — turns red and drains to 0 during cooldown) /
//   Today (green, remaining drains) / Dota 2 week (red, remaining drains,
//   "forbidden" on Mon / Wed / Fri — Dota banned, Steam + other games continue).
// Remaining time lives INSIDE each bar; 0 / max scale sits right below it.
public class MainForm : Form
{
    // Owner-drawn so we can do green/red fills + centered remaining text.
    // WinForms ProgressBar can't do either with visual styles on.
    private sealed class TimeBar : Control
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double MaximumSeconds { get; set; } = 1;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double RemainingSeconds { get; set; } = 0;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color FillColor { get; set; } = Color.FromArgb(0x2E, 0xA8, 0x42);
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string DisplayText { get; set; } = "";
        // Optional playable-up-to marker (session bar): white track to the
        // marker, hatched dead zone after it. ShowLimit=false = plain track.
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool ShowLimit { get; set; }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double LimitSeconds { get; set; } = double.NaN;

        public TimeBar()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var rect = ClientRectangle;
            double max = MaximumSeconds <= 0 ? 1 : MaximumSeconds;
            double frac = Math.Clamp(RemainingSeconds / max, 0, 1);
            int fillW = (int)Math.Round(rect.Width * frac);

            using (var bg = new SolidBrush(Color.FromArgb(0xE2, 0xE2, 0xE2)))
                g.FillRectangle(bg, rect);
            if (ShowLimit)
            {
                double lim = double.IsNaN(LimitSeconds) ? 0 : Math.Clamp(LimitSeconds / max, 0, 1);
                int limitW = (int)Math.Round(rect.Width * lim);
                if (limitW > 0)
                {
                    using var ok = new SolidBrush(Color.White);
                    g.FillRectangle(ok, 0, 0, limitW, rect.Height);
                }
                if (limitW < rect.Width)
                {
                    using var hatch = new System.Drawing.Drawing2D.HatchBrush(
                        System.Drawing.Drawing2D.HatchStyle.BackwardDiagonal,
                        Color.FromArgb(0xD3, 0x2F, 0x2F), Color.Transparent);
                    g.FillRectangle(hatch, limitW, 0, rect.Width - limitW, rect.Height);
                }
            }
            if (fillW > 0)
            {
                using var fill = new SolidBrush(FillColor);
                g.FillRectangle(fill, 0, 0, fillW, rect.Height);
            }
            using (var pen = new Pen(Color.FromArgb(0x99, 0x99, 0x99)))
                g.DrawRectangle(pen, 0, 0, rect.Width - 1, rect.Height - 1);

            // Two-tone centered text: white over the fill, dark over the empty
            // part, so it stays readable at any level (even near-empty).
            string text = string.IsNullOrEmpty(DisplayText) ? "" : DisplayText;
            if (text.Length > 0)
            {
                var fmt = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                };
                var full = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
                if (fillW > 0)
                {
                    g.SetClip(new Rectangle(0, 0, fillW, rect.Height));
                    using var white = new SolidBrush(Color.White);
                    g.DrawString(text, Font, white, full, fmt);
                    g.ResetClip();
                }
                if (fillW < rect.Width)
                {
                    g.SetClip(new Rectangle(fillW, 0, rect.Width - fillW, rect.Height));
                    using var dark = new SolidBrush(Color.FromArgb(0x33, 0x33, 0x33));
                    g.DrawString(text, Font, dark, full, fmt);
                    g.ResetClip();
                }
            }
            base.OnPaint(e);
        }
    }

    private static readonly Color SessionGreen = Color.FromArgb(0x2E, 0xA8, 0x42);
    private static readonly Color DailyBlue = Color.FromArgb(0x1A, 0x73, 0xD9);
    private static readonly Color DotaBlue = Color.FromArgb(0x1A, 0x73, 0xD9);
    private static readonly Color CooldownRed = Color.FromArgb(0xD3, 0x2F, 0x2F);

    private const int BarWidth = 360;
    private const int BarHeight = 26;

    private readonly Label _sessionTitle = new() { AutoSize = true };
    private readonly TimeBar _sessionBar = new() { FillColor = SessionGreen, Size = new Size(BarWidth, BarHeight) };
    private readonly Label _sessionMin = new() { AutoSize = true, Text = "0", ForeColor = SystemColors.GrayText };
    private readonly Label _sessionMid = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _sessionMax = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    private readonly Label _dailyTitle = new() { AutoSize = true };
    private readonly TimeBar _dailyBar = new() { FillColor = DailyBlue, Size = new Size(BarWidth, BarHeight) };
    private readonly Label _dailyMin = new() { AutoSize = true, Text = "0", ForeColor = SystemColors.GrayText };
    private readonly Label _dailyMax = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    private readonly Label _dotaTitle = new() { AutoSize = true };
    private readonly TimeBar _dotaBar = new() { FillColor = DotaBlue, Size = new Size(BarWidth, BarHeight) };
    private readonly Label _dotaMin = new() { AutoSize = true, Text = "0", ForeColor = SystemColors.GrayText };
    private readonly Label _dotaMax = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(360, 0) };
    private readonly Label _detail = new() { AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = SystemColors.GrayText };
    private readonly Button _recheckBtn = new() { Text = "Recheck now", AutoSize = true };
    private readonly Button _killBtn = new() { Text = "Force close Steam + games", AutoSize = true };
    private readonly Button _borrowBtn = new() { Text = "+10m from tomorrow (95% effective)", AutoSize = true, UseVisualStyleBackColor = false };
    private readonly Label _tomorrowLabel = new() { AutoSize = true, ForeColor = SystemColors.ControlText };
    private readonly Button _dotaBorrowBtn = new() { Text = "+10m from next week (95% effective)", AutoSize = true, UseVisualStyleBackColor = false };
    private readonly Label _nextWeekLabel = new() { AutoSize = true, ForeColor = SystemColors.ControlText };
    private readonly Label _detectorLabel = new() { AutoSize = true, Text = "Detector: —" };
    private readonly Button _detailsToggle = new() { Text = "Details ▸", AutoSize = true };
    private readonly FlowLayoutPanel _detailsPanel = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, MaximumSize = new Size(BarWidth + 20, 0), Visible = false };
    private readonly Label _todayDetailLabel = new() { AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = SystemColors.GrayText };
    private readonly Label _detectorDetailLabel = new() { AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = SystemColors.GrayText };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _syncTimer = new() { Interval = 15000 };
    private PipeResponse? _last;
    private DateTime _lastPollLocal = DateTime.Now;
    private string? _lastNote;
    private bool _serviceOk = true;
    private bool _busy;

    public MainForm()
    {
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Steam Timer";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScroll = true;
        ClientSize = new Size(400, 360);

        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
        };

        body.Controls.Add(_sessionTitle);
        body.Controls.Add(_sessionBar);
        body.Controls.Add(ScaleRow(_sessionMin, _sessionMax, _sessionMid));
        body.Controls.Add(_dailyTitle);
        body.Controls.Add(_dailyBar);
        body.Controls.Add(ScaleRow(_dailyMin, _dailyMax));

        var sep = new Panel
        {
            Size = new Size(BarWidth, 2),
            BackColor = SystemColors.ControlDark,
            Margin = new Padding(0, 8, 0, 8),
        };
        body.Controls.Add(sep);

        body.Controls.Add(_dotaTitle);
        body.Controls.Add(_dotaBar);
        body.Controls.Add(ScaleRow(_dotaMin, _dotaMax));

        _status.Margin = new Padding(0, 8, 0, 8);
        body.Controls.Add(_status);
        body.Controls.Add(_detectorLabel);

        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
        };
        _recheckBtn.Margin = new Padding(0, 0, 8, 0);
        _recheckBtn.Click += async (_, _) => await RecheckAsync();
        _killBtn.Click += async (_, _) => await ForceCloseAsync();
        row.Controls.Add(_recheckBtn);
        row.Controls.Add(_killBtn);
        body.Controls.Add(row);

        var row2 = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
        };
        _borrowBtn.Margin = new Padding(0, 8, 0, 0);
        _borrowBtn.Click += async (_, _) => await BorrowAsync();
        row2.Controls.Add(_borrowBtn);
        _tomorrowLabel.Margin = new Padding(8, 14, 0, 0);
        _tomorrowLabel.Text = "Tomorrow: —";
        row2.Controls.Add(_tomorrowLabel);
        body.Controls.Add(row2);

        var row3 = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
        };
        _dotaBorrowBtn.Margin = new Padding(0, 8, 0, 0);
        _dotaBorrowBtn.Click += async (_, _) => await BorrowDotaAsync();
        row3.Controls.Add(_dotaBorrowBtn);
        _nextWeekLabel.Margin = new Padding(8, 14, 0, 0);
        _nextWeekLabel.Text = "Next week: —";
        row3.Controls.Add(_nextWeekLabel);
        body.Controls.Add(row3);

        // Collapsed secondary info (was: "?" popups + gray detail lines).
        _detailsToggle.Margin = new Padding(0, 8, 0, 0);
        _detailsToggle.Click += (_, _) =>
        {
            _detailsPanel.Visible = !_detailsPanel.Visible;
            _detailsToggle.Text = _detailsPanel.Visible ? "Details ▾" : "Details ▸";
        };
        body.Controls.Add(_detailsToggle);
        _detailsPanel.Margin = new Padding(0, 4, 0, 0);
        _detailsPanel.FlowDirection = FlowDirection.TopDown;
        _detailsPanel.WrapContents = false;
        _detailsPanel.AutoSize = true;
        _todayDetailLabel.Margin = new Padding(0, 0, 0, 4);
        _detectorDetailLabel.Margin = new Padding(0, 0, 0, 4);
        _detail.Margin = new Padding(0, 0, 0, 0);
        _detailsPanel.Controls.Add(_todayDetailLabel);
        _detailsPanel.Controls.Add(_detectorDetailLabel);
        _detailsPanel.Controls.Add(_detail);
        body.Controls.Add(_detailsPanel);

        Controls.Add(body);

        // Keep the existing compact width; shorter desktops scroll.
        Load += (_, _) =>
        {
            var work = Screen.FromControl(this).WorkingArea;
            ClientSize = new Size(Math.Max(400, body.PreferredSize.Width + SystemInformation.VerticalScrollBarWidth),
                Math.Min(body.PreferredSize.Height, work.Height - (Height - ClientSize.Height) - 32));
            MinimumSize = new Size((int)Math.Round(416 * DeviceDpi / 96d),
                Math.Min(work.Height, (int)Math.Round(360 * DeviceDpi / 96d)));
            Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + Math.Max(0, (work.Height - Height) / 2));
        };

        _uiTimer.Tick += (_, _) => RefreshCountdown();
        _uiTimer.Start();
        _ = SyncFromServiceAsync();
        _syncTimer.Tick += async (_, _) => await SyncFromServiceAsync();
        _syncTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiTimer.Dispose();
            _syncTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    // "0" left, max right, optional mid centered, right below its bar.
    private static Panel ScaleRow(Label min, Label max, Label? mid = null)
    {
        var p = new Panel
        {
            Size = new Size(BarWidth, 16),
            Margin = new Padding(0, 0, 0, 8),
        };
        min.Location = new Point(0, 0);
        max.AutoSize = true;
        // Right-align: park it, RefreshCountdown corrects the X once text is set.
        max.Location = new Point(BarWidth - 60, 0);
        max.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        p.Controls.Add(min);
        if (mid != null)
        {
            mid.Location = new Point(BarWidth / 2, 0);
            p.Controls.Add(mid);
        }
        p.Controls.Add(max);
        return p;
    }

    private static void SetMaxLabel(Label l, string text)
    {
        l.Text = text;
        // Use the scaled row width so the labels align at high DPI too.
        var w = TextRenderer.MeasureText(text, l.Font).Width;
        l.Location = new Point(Math.Max(0, (l.Parent?.ClientSize.Width ?? BarWidth) - w), 0);
    }

    private static void SetMidLabel(Label l, string text)
    {
        l.Text = text;
        // Keep it centered under the scaled bar.
        var w = TextRenderer.MeasureText(text, l.Font).Width;
        l.Location = new Point(Math.Max(0, ((l.Parent?.ClientSize.Width ?? BarWidth) - w) / 2), 0);
    }

    private async Task SyncFromServiceAsync()
    {
        var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "status" });
        if (resp == null || !resp.Ok)
        {
            _serviceOk = false;
            RefreshCountdown();
            return;
        }
        _serviceOk = true;
        UpdateFromStatus(resp);
    }

    private async Task RecheckAsync()
    {
        if (_busy) return;
        _busy = true;
        _recheckBtn.Enabled = false;
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "recheck" });
            if (resp == null || !resp.Ok)
            {
                _serviceOk = false;
                _lastNote = "Recheck failed — service unreachable. Is SteamTimer running?";
                RefreshCountdown();
                return;
            }
            _serviceOk = true;
            _lastNote = resp.ActionNote;
            UpdateFromStatus(resp, keepNote: true);
        }
        finally { _busy = false; _recheckBtn.Enabled = true; }
    }

    private async Task ForceCloseAsync()
    {
        if (_busy) return;
        string what = _last != null && (_last.LiveClients.Count > 0 || _last.LiveGames.Count > 0 || _last.MatchedOrphans.Count > 0)
            ? $"The service currently sees: {DescribeSeen(_last)}.\n\n"
            : "";
        var confirm = MessageBox.Show(
            $"{what}Force-close Steam + detected games now? This stops the timer immediately " +
            $"(the normal cooldown for the time played still applies).",
            "Steam Timer — force close", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;
        _busy = true;
        _killBtn.Enabled = false;
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "kill" }, timeoutMs: 15000);
            if (resp == null || !resp.Ok)
            {
                _serviceOk = false;
                _lastNote = "Force-close failed — service unreachable. Is SteamTimer running?";
                RefreshCountdown();
                MessageBox.Show(_lastNote, "Steam Timer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _serviceOk = true;
            _lastNote = resp.ActionNote;
            UpdateFromStatus(resp, keepNote: true);
            MessageBox.Show(resp.ActionNote, "Steam Timer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally { _busy = false; _killBtn.Enabled = true; }
    }

    // One click, no confirm, no success popup — the bars + button update.
    // Errors (Sundays / tomorrow empty / service down) still show a dialog.
    private async Task BorrowAsync()
    {
        if (_busy) return;
        // Sunday pre-check (service is the source of truth and re-checks).
        if (_last != null && DateTime.TryParse(_last.LogicalDay, out var ld) && ld.DayOfWeek == DayOfWeek.Sunday)
        {
            MessageBox.Show("Borrow is unavailable on Sundays — enjoy the 4h budget.",
                "Steam Timer — borrow", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _busy = true;
        _borrowBtn.Enabled = false;
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "borrow" }, timeoutMs: 15000);
            if (resp == null)
            {
                _serviceOk = false;
                _lastNote = "Borrow failed — service unreachable. Is SteamTimer running?";
                RefreshCountdown();
                MessageBox.Show(_lastNote, "Steam Timer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!resp.Ok)
            {
                _serviceOk = true;
                _lastNote = resp.Error;
                UpdateFromStatus(resp, keepNote: false);
                _lastNote = resp.Error;
                RefreshCountdown();
                MessageBox.Show(resp.Error, "Steam Timer — borrow", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _serviceOk = true;
            _lastNote = null; // no success popup by design; detail line stays clean
            UpdateFromStatus(resp, keepNote: false);
        }
        finally { _busy = false; _borrowBtn.Enabled = true; }
    }

    // Dota borrow: one click, no confirm, no success popup — the bar + button update.
    // Errors (next week empty / service down) still show a dialog.
    private async Task BorrowDotaAsync()
    {
        if (_busy) return;
        _busy = true;
        _dotaBorrowBtn.Enabled = false;
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "borrowdota" }, timeoutMs: 15000);
            if (resp == null)
            {
                _serviceOk = false;
                _lastNote = "Dota borrow failed — service unreachable. Is SteamTimer running?";
                RefreshCountdown();
                MessageBox.Show(_lastNote, "Steam Timer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!resp.Ok)
            {
                _serviceOk = true;
                _lastNote = resp.Error;
                UpdateFromStatus(resp, keepNote: false);
                _lastNote = resp.Error;
                RefreshCountdown();
                MessageBox.Show(resp.Error, "Steam Timer — Dota borrow", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _serviceOk = true;
            _lastNote = null; // no success popup by design; detail line stays clean
            UpdateFromStatus(resp, keepNote: false);
        }
        finally { _busy = false; _dotaBorrowBtn.Enabled = true; }
    }

    private static string DescribeSeen(PipeResponse r)
    {
        var bits = new List<string>();
        if (r.LiveClients.Count > 0) bits.Add("client: " + string.Join(", ", r.LiveClients));
        if (r.LiveGames.Count > 0) bits.Add("games: " + string.Join(", ", r.LiveGames));
        if (r.MatchedOrphans.Count > 0) bits.Add("orphans: " + string.Join(", ", r.MatchedOrphans));
        return bits.Count > 0 ? string.Join("; ", bits) : "nothing running";
    }

    private static string Hm(double totalSeconds)
    {
        // Never throw: a bad value must not kill the UI timer.
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds)) return "—";
        if (totalSeconds < 0) totalSeconds = 0;
        if (totalSeconds > TimeSpan.MaxValue.TotalSeconds) totalSeconds = TimeSpan.MaxValue.TotalSeconds;
        var t = TimeSpan.FromSeconds(totalSeconds);
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }

    // Precise countdown for the short cooldown bar (ticks every second).
    // Hm() truncates to minutes, which would look frozen for a whole minute.
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

    // Called by the tray poll too, so the window stays correct when hidden.
    // Display only — balloons are owned by TrayContext.
    public void UpdateFromStatus(PipeResponse resp, bool keepNote = false)
    {
        if (IsDisposed || _last != null && resp.ServerNowUtc < _last.ServerNowUtc) return;
        _last = resp;
        _lastPollLocal = DateTime.Now;
        _serviceOk = true;
        if (!keepNote) _lastNote = string.IsNullOrWhiteSpace(resp.ActionNote) ? _lastNote : resp.ActionNote;
        RefreshCountdown();
    }

    // X hides to tray instead of closing, so countdown + status keep working.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    // Button label + drama: regular face at 95%, heating toward red as the
    // next press's effectiveness sinks to the 0.1% floor.
    private void RefreshBorrowButton(PipeResponse r)
    {
        double eff = r.NextBorrowEffectiveness > 0
            ? r.NextBorrowEffectiveness
            : Limits.NextBorrowEffectiveness(r.BorrowCount);
        _borrowBtn.Text = Limits.BorrowButtonText(r.BorrowCount);
        double t = 1 - (eff - Limits.BorrowMinEffectiveness)
            / (Limits.BorrowFirstEffectiveness - Limits.BorrowMinEffectiveness);
        t = Math.Clamp(t, 0, 1);
        var start = SystemColors.Control;
        var end = Color.FromArgb(0xD3, 0x2F, 0x2F);
        _borrowBtn.BackColor = Color.FromArgb(
            (int)(start.R + (end.R - start.R) * t),
            (int)(start.G + (end.G - start.G) * t),
            (int)(start.B + (end.B - start.B) * t));
        _borrowBtn.ForeColor = t > 0.5 ? Color.White : SystemColors.ControlText;
        _tomorrowLabel.Text = $"Tomorrow: {Hm(TomorrowRemainingSeconds(r))}";
    }

    // Dota button: same decay, but heating runs 95% -> 50% floor.
    private void RefreshDotaBorrowButton(PipeResponse r)
    {
        double eff = r.NextDotaBorrowEffectiveness > 0
            ? r.NextDotaBorrowEffectiveness
            : Limits.NextDotaBorrowEffectiveness(r.DotaBorrowCount);
        _dotaBorrowBtn.Text = Limits.DotaBorrowButtonText(r.DotaBorrowCount);
        double t = 1 - (eff - Limits.DotaBorrowMinEffectiveness)
            / (Limits.BorrowFirstEffectiveness - Limits.DotaBorrowMinEffectiveness);
        t = Math.Clamp(t, 0, 1);
        var start = SystemColors.Control;
        var end = Color.FromArgb(0xD3, 0x2F, 0x2F);
        _dotaBorrowBtn.BackColor = Color.FromArgb(
            (int)(start.R + (end.R - start.R) * t),
            (int)(start.G + (end.G - start.G) * t),
            (int)(start.B + (end.B - start.B) * t));
        _dotaBorrowBtn.ForeColor = t > 0.5 ? Color.White : SystemColors.ControlText;
        _nextWeekLabel.Text = $"Next week: {Hm(NextWeekDotaRemainingSeconds(r))}";
    }

    private static double TomorrowRemainingSeconds(PipeResponse r)
    {
        double tomorrowBase = r.TomorrowBaseSeconds > 0
            ? r.TomorrowBaseSeconds
            : Limits.DailyBaseFor(DateTime.Now.AddDays(1).DayOfWeek).TotalSeconds;
        double debt = r.BorrowCount * Limits.BorrowCost.TotalSeconds;
        return Math.Max(0, tomorrowBase - debt);
    }

    private static double NextWeekDotaRemainingSeconds(PipeResponse r)
    {
        double nextBase = Limits.DotaWeeklyBudget.TotalSeconds;
        double debt = r.DotaBorrowCount * Limits.BorrowCost.TotalSeconds;
        return Math.Max(0, nextBase - debt);
    }

    private void RefreshCountdown()
    {
        if (_last == null)
        {
            _sessionTitle.Text = "Session";
            _sessionBar.MaximumSeconds = Limits.SessionMax.TotalSeconds;
            _sessionBar.RemainingSeconds = 0;
            _sessionBar.DisplayText = _serviceOk ? "waiting…" : "no service";
            _sessionBar.ShowLimit = false;
            SetMaxLabel(_sessionMax, Hm(Limits.SessionMax.TotalSeconds));
            SetMidLabel(_sessionMid, "—");
            _dailyTitle.Text = "Today";
            _dailyBar.MaximumSeconds = 1;
            _dailyBar.RemainingSeconds = 0;
            _dailyBar.DisplayText = _serviceOk ? "waiting…" : "no service";
            SetMaxLabel(_dailyMax, "—");
            _dotaTitle.Text = "Dota 2 — this week";
            _dotaBar.MaximumSeconds = Limits.DotaWeeklyBudget.TotalSeconds;
            _dotaBar.RemainingSeconds = 0;
            _dotaBar.DisplayText = _serviceOk ? "waiting…" : "no service";
            SetMaxLabel(_dotaMax, Hm(Limits.DotaWeeklyBudget.TotalSeconds));
            _status.Text = _serviceOk ? "Waiting for the service…" : "Service unreachable — is SteamTimer running?";
            _status.Visible = true;
            _detail.Text = _serviceOk ? "" : "Start the Windows service \"SteamTimer\", then hit Recheck now.";
            _todayDetailLabel.Text = _serviceOk ? "Waiting for the service…" : _detail.Text;
            _detectorDetailLabel.Text = "";
            _detectorLabel.Text = "Detector: —";
            _tomorrowLabel.Text = "Tomorrow: —";
            _nextWeekLabel.Text = "Next week: —";
            return;
        }
        var r = _last;
        RefreshBorrowButton(r);
        RefreshDotaBorrowButton(r);
        // Estimate live values between 15s polls while things run.
        double elapsed = (DateTime.Now - _lastPollLocal).TotalSeconds;
        double remaining = r.RemainingSeconds;
        if (r.Running && remaining > 0) remaining = Math.Max(0, remaining - elapsed);
        double sessElapsed = r.SessionElapsedSeconds + (r.Running ? elapsed : 0);
        double dotaRem = r.DotaRemainingSeconds;
        if (r.DotaRunning && dotaRem > 0) dotaRem = Math.Max(0, dotaRem - elapsed);

        // ---- bars: session fills up with played time; today/Dota drain down ----
        double sessionMax = r.SessionMaxSeconds > 0 ? r.SessionMaxSeconds : Limits.SessionMax.TotalSeconds;
        double dailyMax = r.BudgetSeconds > 0 ? r.BudgetSeconds : 1;
        double dotaMax = r.DotaBudgetSeconds > 0 ? r.DotaBudgetSeconds : Limits.DotaWeeklyBudget.TotalSeconds;

        bool blocked = r.Enforcing || remaining <= 0;
        bool cooling = !blocked && r.InCooldown;

        // Session bar fills UP with played time toward the 2h session max.
        // Cooldown keeps the old drain-to-zero red bar.
        double sessFill;
        double sessCap = sessionMax;
        double sessLimit = double.NaN;
        string sessText;
        Color sessColor = SessionGreen;
        string sessMaxText = Hm(sessionMax);
        if (blocked)
        {
            sessFill = 0;
            sessText = "blocked";
            sessLimit = 0;
        }
        else if (cooling)
        {
            // Red bar that drains as the cooldown ticks to 0 (1s UI timer).
            double cd = Math.Max(0, r.CooldownRemainingSeconds - elapsed);
            double total = r.CooldownTotalSeconds > 0
                ? r.CooldownTotalSeconds
                : Limits.CooldownForSession(TimeSpan.FromSeconds(r.LastSessionSeconds)).TotalSeconds;
            if (total <= 0) total = Math.Max(cd, 1);
            if (cd > total) total = cd;
            sessFill = cd;
            sessCap = total;
            sessColor = CooldownRed;
            sessText = cd <= 0 ? "ready" : $"cooldown {FormatCooldown(cd)}";
            sessMaxText = Hm(total);
        }
        else if (r.Running)
        {
            // Scale is always the 2h session max; the fill is played time.
            sessCap = sessionMax;
            sessFill = Math.Min(sessElapsed, sessCap);
            sessMaxText = Hm(sessCap);
            // Reachable end of this session on today's budget; a borrow press
            // pushes it out and the hatched zone shrinks.
            sessLimit = Math.Min(sessionMax, sessElapsed + remaining);
            sessText = $"{FormatCooldown(sessFill)} played";
        }
        else
        {
            sessCap = sessionMax;
            sessFill = 0;
            sessMaxText = Hm(sessCap);
            sessLimit = Math.Min(sessionMax, remaining);
            sessText = "ready";
        }

        string dailyText = (r.Enforcing || remaining <= 0) ? "used up" : $"{Hm(remaining)} left";
        string dotaText = r.DotaForbidden ? "forbidden" : r.DotaEnforcing ? "used up" : $"{Hm(dotaRem)} left";

        _sessionTitle.Text = r.Running ? "Session - playing" : "Session";
        _sessionBar.FillColor = sessColor;
        _sessionBar.MaximumSeconds = sessCap;
        _sessionBar.RemainingSeconds = sessFill;
        _sessionBar.LimitSeconds = sessLimit;
        _sessionBar.ShowLimit = !double.IsNaN(sessLimit);
        _sessionBar.DisplayText = sessText;
        _sessionBar.Invalidate();
        SetMaxLabel(_sessionMax, sessMaxText);
        SetMidLabel(_sessionMid, double.IsNaN(sessLimit) ? "" : Hm(Math.Max(0, sessLimit)));

        _dailyTitle.Text = "Today";
        _dailyBar.MaximumSeconds = dailyMax;
        _dailyBar.RemainingSeconds = remaining;
        _dailyBar.DisplayText = dailyText;
        _dailyBar.Invalidate();
        SetMaxLabel(_dailyMax, Hm(dailyMax));

        _dotaTitle.Text = r.DotaForbidden ? "Dota 2 — forbidden Mon / Wed / Fri" : r.DotaRunning ? "Dota 2 - running" : "Dota 2";
        _dotaBar.MaximumSeconds = dotaMax;
        _dotaBar.RemainingSeconds = r.DotaForbidden ? 0 : dotaRem;
        _dotaBar.DisplayText = dotaText;
        _dotaBar.Invalidate();
        SetMaxLabel(_dotaMax, Hm(dotaMax));

        // ---- kept text info below the bars ----
        // Blocked/cooldown state lives in the bars already (session bar shows
        // "blocked" / "cooldown Xm Ys"); no extra cooldown/blocked lines here.
        if (r.Enforcing || remaining <= 0)
        {
            _status.Text = "Blocked — daily budget used up.";
            _status.Visible = true;
            TrayContext.Instance?.SetState("Steam blocked until 03:00", TimeSpan.Zero);
        }
        else if (r.InCooldown)
        {
            double cd = Math.Max(0, r.CooldownRemainingSeconds - elapsed);
            _status.Text = "";
            _status.Visible = false;
            TrayContext.Instance?.SetState($"Cooldown: {Hm(cd)} left", TimeSpan.Zero);
        }
        else
        {
            string sessionLine = r.Running
                ? $"This session ends in {Hm(Math.Min(Math.Max(0, sessionMax - sessElapsed), Math.Max(0, remaining)))}, then a cooldown."
                : "";
            _status.Text = sessionLine;
            _status.Visible = !string.IsNullOrEmpty(sessionLine);
            var left = TimeSpan.FromSeconds(remaining);
            string tip = (r.Running ? $"Steam: {Hm(remaining)} left today" : $"Paused: {Hm(remaining)} left today")
                + (r.DotaForbidden ? " • Dota forbidden M/W/F" : r.DotaEnforcing ? " • Dota done" : "");
            TrayContext.Instance?.SetState(tip, left);
        }

        _detail.Text = BuildDetail(r, elapsed);

        _todayDetailLabel.Text = $"Budget {Hm(dailyMax)} total\nBase {Hm(r.BaseBudgetSeconds)} + carry {Hm(r.CarryInSeconds)}" +
            (r.BorrowedSeconds > 0 ? $" + borrow {Hm(r.BorrowedSeconds)}" : "") +
            (r.AdvanceDeductionSeconds > 0 ? $" − advance {Hm(r.AdvanceDeductionSeconds)}" : "") +
            $"\nUsed {Hm(r.UsedSeconds)}, left {Hm(remaining)}" +
            $"\n\nDota {Hm(dotaMax)} total\nBase {Hm(r.DotaBaseBudgetSeconds > 0 ? r.DotaBaseBudgetSeconds : Limits.DotaWeeklyBudget.TotalSeconds)}" +
            (r.DotaBorrowedSeconds > 0 ? $" + borrow {Hm(r.DotaBorrowedSeconds)}" : "") +
            (r.DotaAdvanceDeductionSeconds > 0 ? $" − advance {Hm(r.DotaAdvanceDeductionSeconds)}" : "") +
            $"\nUsed {Hm(r.DotaUsedSeconds)}, left {Hm(dotaRem)}" +
            (r.DotaForbidden ? "\nNo Dota on Mon / Wed / Fri." : "");

        _detectorLabel.Text = r.SteamRunning ? "Detector: Steam detected" : "Detector: nothing detected";
        _detectorDetailLabel.Text = "Sees now: " + DescribeSeen(r) + ".\n" +
            (r.RememberedGames.Count > 0 ? "Remembered: " + string.Join(", ", r.RememberedGames) + "." : "Remembered: none.") +
            $"\nLast poll {Math.Max(0, (int)elapsed)}s ago.";
    }

    private string BuildDetail(PipeResponse r, double elapsed)
    {
        var lines = new List<string>();
        if (!_serviceOk)
            lines.Add("Service unreachable — showing last known state. Hit Recheck now.");
        // Session state vs live detector: the two can legitimately differ for up
        // to ~15s (service tick), which used to look like "it never stops".
        // Process/PID details live in the Details section below.
        string sess = r.Running ? "session OPEN (accruing)" : r.InCooldown ? "in cooldown (blocked)" : "no session (stopped)";
        lines.Add($"Timer {sess}.");
        int age = Math.Max(0, (int)elapsed);
        lines.Add($"Last poll {age}s ago — Recheck asks the service right now.");
        if (!string.IsNullOrWhiteSpace(_lastNote))
            lines.Add(_lastNote!);
        return string.Join("\n", lines);
    }
}
