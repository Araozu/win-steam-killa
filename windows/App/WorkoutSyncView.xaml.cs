using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shared;

namespace App;

public sealed partial class WorkoutSyncView : UserControl
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private WorkoutSyncStatus? _status;
    private string? _note;
    private string? _problem;
    private bool _open;
    private bool _busy;
    private bool _polling;
    private bool _loadedSettings;
    private int _revision;

    public event EventHandler? BackRequested;

    public WorkoutSyncView()
    {
        InitializeComponent();
        _poll.Tick += async (_, _) => await RefreshAsync();
    }

    public void OpenScreen()
    {
        _open = true;
        _loadedSettings = false;
        _status = null;
        _note = null;
        _problem = null;
        ReceiverUrl.Text = "";
        Render();
        _poll.Start();
        _ = RefreshAsync();
    }

    public void CloseScreen()
    {
        _open = false;
        ++_revision;
        _poll.Stop();
        ReceiverToken.Password = "";
    }

    private async Task RefreshAsync()
    {
        if (!_open || _busy || _polling) return;
        _polling = true;
        int revision = _revision;
        try
        {
            var reply = await PipeClient.SendAsync(new PipeRequest { Cmd = "workout-status" });
            if (_open && revision == _revision)
                ApplyReply(reply);
        }
        finally { _polling = false; }
    }

    private void ApplyReply(PipeReply reply)
    {
        if (reply.Response?.WorkoutSync is not { } status)
        {
            _status = null;
            _problem = reply.Response is null ? reply.Problem : "Update the Windows service to use workout settings.";
        }
        else
        {
            _status = status;
            _problem = null;
            if (!_loadedSettings)
            {
                _loadedSettings = true;
                ReceiverUrl.Text = status.BaseUrl;
            }
        }
        Render();
    }

    private async void Save_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(new PipeRequest
        {
            Cmd = "workout-save",
            WorkoutBaseUrl = ReceiverUrl.Text,
            WorkoutApiToken = ReceiverToken.Password
        });

    private async void Sync_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(new PipeRequest { Cmd = "workout-sync" });

    private async Task RunAsync(PipeRequest request)
    {
        if (_busy) return;
        _busy = true;
        _note = null;
        _problem = null;
        int revision = ++_revision;
        Render();
        try
        {
            var reply = await PipeClient.SendAsync(request);
            if (!_open || revision != _revision) return;
            if (reply.Response is { WorkoutSync: not null, Ok: true } response)
            {
                if (request.Cmd == "workout-save")
                {
                    _loadedSettings = false;
                    ReceiverToken.Password = "";
                    _note = "Settings saved.";
                }
            }
            else
                _note = reply.Response?.Error ?? reply.Problem;
            ApplyReply(reply);
        }
        finally
        {
            _busy = false;
            if (_open) Render();
        }
    }

    private void Render()
    {
        bool syncing = _status?.Syncing == true;
        bool canEdit = _status is not null && !_busy && !syncing;
        bool edited = ReceiverUrl.Text.Trim() != (_status?.BaseUrl ?? "") || ReceiverToken.Password.Length > 0;
        ReceiverUrl.IsEnabled = ReceiverToken.IsEnabled = canEdit;
        SaveButton.IsEnabled = canEdit && edited && !string.IsNullOrWhiteSpace(ReceiverUrl.Text);
        SyncButton.IsEnabled = canEdit && _status?.HasToken == true
            && !string.IsNullOrWhiteSpace(_status.BaseUrl) && !edited;
        ReceiverToken.PlaceholderText = _status?.HasToken == true ? "Saved · enter a token to replace it" : "Enter the receiver token";
        LastSuccess.Text = _status?.LastSuccessUtc is { } last && last != default
            ? $"Last successful sync · {last.ToLocalTime():g}" : "Last successful sync · —";
        EarnedCredit.Text = _status is { } status
            ? $"Dota credit earned this week · {TimeText.Hm(status.EarnedSeconds)}"
            : "Dota credit earned this week · —";
        SyncProgress.IsActive = syncing || _busy;
        SyncProgress.Visibility = SyncProgress.IsActive ? Visibility.Visible : Visibility.Collapsed;
        Feedback.Text = syncing ? "Syncing workouts…" : _problem ?? _note ?? _status?.LastResult
            ?? (_status is null ? "Connecting to the Windows service…" : _status.HasToken ? "" : "Save the receiver URL and token to enable sync.");
    }

    private void UrlChanged(object sender, TextChangedEventArgs e) { if (_open) Render(); }
    private void TokenChanged(object sender, RoutedEventArgs e) { if (_open) Render(); }
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}
