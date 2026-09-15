using Shared;

namespace App;

public class MainForm : Form
{
    private readonly TextBox _input = new() { Width = 140, PlaceholderText = "e.g. 30m (session cap)" };
    private readonly Button _start = new() { Text = "Cap this session", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(340, 0) };
    private readonly FlowLayoutPanel _presets = new() { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10) };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    private PipeResponse? _last;
    private DateTime _lastPollLocal = DateTime.Now;

    public MainForm()
    {
        Text = "Steam Timer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(380, 260);

        var mid = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10) };
        mid.Controls.Add(_input);
        mid.Controls.Add(_start);
        _status.Padding = new Padding(10);
        _status.Dock = DockStyle.Fill;
        Controls.Add(_status);
        Controls.Add(mid);
        Controls.Add(_presets);

        _start.Click += OnStart;
        _uiTimer.Tick += (_, _) => RefreshCountdown();
        _uiTimer.Start();
        _ = SyncFromServiceAsync();
        var sync = new System.Windows.Forms.Timer { Interval = 15000 };
        sync.Tick += async (_, _) => await SyncFromServiceAsync();
        sync.Start();
    }

    private static string[] PresetsFor(int budgetSec) =>
        budgetSec <= 5400 ? ["30m", "1h", "1h30m"]
        : budgetSec <= 9000 ? ["30m", "1h", "1h30m", "2h30m"]
        : ["1h", "1h30m", "2h", "3h"];

    private void RebuildPresets(int budgetSec)
    {
        var want = PresetsFor(budgetSec);
        var have = _presets.Controls.OfType<Button>().Select(b => b.Text).ToArray();
        if (have.SequenceEqual(want)) return;
        _presets.Controls.Clear();
        foreach (var p in want)
        {
            var b = new Button { Text = p, AutoSize = true };
            b.Click += (_, _) => { _input.Text = p; };
            _presets.Controls.Add(b);
        }
    }

    private async void OnStart(object? sender, EventArgs e)
    {
        if (!DurationParser.TryParse(_input.Text, out var d, out var err))
        {
            MessageBox.Show(err, "Steam Timer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _start.Enabled = false;
        _start.Text = "Starting...";
        try
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "start", Seconds = (int)d.TotalSeconds });
            if (resp == null)
            {
                MessageBox.Show("Service not reachable. Is SteamTimer installed and running?\nRun Setup/install.ps1 as Administrator.",
                    "Steam Timer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!resp.Ok)
            {
                MessageBox.Show(resp.Error, "Steam Timer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            await SyncFromServiceAsync();
        }
        finally
        {
            _start.Text = "Cap this session";
            _start.Enabled = _last == null || !_last.Enforcing;
        }
    }

    private async Task SyncFromServiceAsync()
    {
        var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "status" });
        if (resp == null || !resp.Ok) return;
        UpdateFromStatus(resp);
    }

    private static string Hm(double totalSeconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }

    // Called by the tray poll too, so the window stays correct when hidden.
    // Display only — balloons are owned by TrayContext.
    public void UpdateFromStatus(PipeResponse resp)
    {
        _last = resp;
        _lastPollLocal = DateTime.Now;
        RebuildPresets(resp.BudgetSeconds > 0 ? resp.BudgetSeconds : 5400);
        _start.Enabled = !resp.Enforcing;
        _input.Enabled = !resp.Enforcing;
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

    private void RefreshCountdown()
    {
        if (_last == null) return;
        var r = _last;
        // Estimate live remaining between 15s polls while a session runs.
        double elapsed = (DateTime.Now - _lastPollLocal).TotalSeconds;
        double remaining = r.RemainingSeconds;
        if (r.Running && remaining > 0) remaining = Math.Max(0, remaining - elapsed);
        double sessRem = r.SessionCapSeconds > 0 ? r.SessionRemainingSeconds : double.PositiveInfinity;
        if (r.Running && r.SessionCapSeconds > 0) sessRem = Math.Max(0, sessRem - elapsed);

        if (r.Enforcing || remaining <= 0)
        {
            _status.Text = $"Budget used up ({Hm(r.BudgetSeconds)} today). Back after 03:00.\nSteam is blocked until then — the service keeps enforcing.";
            TrayContext.Instance?.SetState("Steam blocked until 03:00", TimeSpan.Zero);
            return;
        }
        string state = r.Running ? "Playing — counting down" : "Paused — Steam closed, timer stopped";
        string cap = r.SessionCapSeconds > 0
            ? $"\nThis session ends in {Hm(sessRem)} (cap {Hm(r.SessionCapSeconds)})."
            : "\nNo session cap — launch Steam and it tracks itself. Type e.g. 30m to cap this session.";
        _status.Text = $"{state}.\nLeft today: {Hm(remaining)} of {Hm(r.BudgetSeconds)} (used {Hm(r.UsedSeconds)}).{cap}";
        var left = TimeSpan.FromSeconds(remaining);
        TrayContext.Instance?.SetState(
            r.Running ? $"Steam: {Hm(remaining)} left today" : $"Paused: {Hm(remaining)} left today", left);
    }
}
