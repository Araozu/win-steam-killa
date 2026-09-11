using Shared;

namespace App;

public class MainForm : Form
{
    private readonly TextBox _input = new() { Width = 140, PlaceholderText = "e.g. 1h30m" };
    private readonly Button _start = new() { Text = "Start timer", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(340, 0) };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    private DateTime _deadlineUtc;
    private bool _hasTimer;
    private bool _enforcing;
    private bool _warned;
    private bool _killTold;

    public MainForm()
    {
        Text = "Steam Timer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(380, 220);

        var presets = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10) };
        foreach (var p in new[] { "1h", "1h30m", "2h", "3h" })
        {
            var b = new Button { Text = p, AutoSize = true };
            b.Click += (_, _) => { _input.Text = p; };
            presets.Controls.Add(b);
        }
        var mid = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10) };
        mid.Controls.Add(_input);
        mid.Controls.Add(_start);
        _status.Padding = new Padding(10);
        _status.Dock = DockStyle.Fill;
        Controls.Add(_status);
        Controls.Add(mid);
        Controls.Add(presets);

        _start.Click += OnStart;
        _uiTimer.Tick += (_, _) => RefreshCountdown();
        _uiTimer.Start();
        _ = SyncFromServiceAsync();
        var sync = new System.Windows.Forms.Timer { Interval = 15000 };
        sync.Tick += async (_, _) => await SyncFromServiceAsync();
        sync.Start();
    }

    private void OnStart(object? sender, EventArgs e)
    {
        if (!DurationParser.TryParse(_input.Text, out var d, out var err))
        {
            MessageBox.Show(err, "Steam Timer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var resp = PipeClient.Send(new PipeRequest { Cmd = "start", Seconds = (int)d.TotalSeconds });
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
        _ = SyncFromServiceAsync();
    }

    private async Task SyncFromServiceAsync()
    {
        var resp = await PipeClient.SendAsync(new PipeRequest { Cmd = "status" });
        if (resp == null || !resp.Ok) return;
        if (!resp.HasTimer)
        {
            _hasTimer = false;
            _status.Text = "No timer today.\nPick 1h / 1h30m / 2h (max 3h). One timer per day until 03:00.";
            _start.Enabled = true;
            _input.Enabled = true;
            TrayContext.Instance?.SetState("No timer today — open Steam Timer to start one?", null);
            return;
        }
        _hasTimer = true;
        _deadlineUtc = resp.DeadlineUtc;
        _enforcing = resp.Enforcing;
        _start.Enabled = false;
        _input.Enabled = false;
        RefreshCountdown();
    }

    private void RefreshCountdown()
    {
        if (!_hasTimer) return;
        var left = _deadlineUtc - DateTime.UtcNow;
        if (left > TimeSpan.Zero)
        {
            _status.Text = $"Time left: {(int)left.TotalHours}h {left.Minutes:00}m {left.Seconds:00}s\nDeadline at {_deadlineUtc.ToLocalTime():HH:mm}. Max 3h, one per day.";
            TrayContext.Instance?.SetState($"Steam: {(int)left.TotalHours}h {left.Minutes:00}m left", left);
            if (left <= Limits.WarnBefore && !_warned)
            {
                _warned = true;
                TrayContext.Instance?.Balloon("Steam closes in 15 minutes", "Save your game.");
            }
        }
        else
        {
            _status.Text = "Time is up. Steam is blocked until 03:00.\nClose this window if you like — the service keeps enforcing.";
            TrayContext.Instance?.SetState("Steam blocked until 03:00", TimeSpan.Zero);
            if (!_killTold)
            {
                _killTold = true;
                TrayContext.Instance?.Balloon("Steam time is up", "Closed Steam. Blocked until 03:00.");
            }
        }
    }
}
