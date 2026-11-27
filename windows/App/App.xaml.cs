using Microsoft.UI.Xaml;
using System.Threading;

namespace App;

public partial class SteamTimerApp : Microsoft.UI.Xaml.Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _openWindow;
    private RegisteredWaitHandle? _activation;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private bool _shuttingDown;

    public SteamTimerApp() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mutex = new Mutex(true, @"Local\SteamTimerApp", out bool first);
        _openWindow = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SteamTimerApp.Open");
        if (!first) { _openWindow.Set(); Exit(); return; }
        // The window and the tray share one lock so only one action (or
        // confirmation dialog) can be in progress at a time.
        var gate = new ActionGate();
        _main = new MainWindow(gate);
        _tray = new TrayIcon(() => _main.ShowMain(), () => _main, gate);
        _main.SetTray(_tray);
        _main.ShowMain();
        var dispatcher = _main.DispatcherQueue;
        _activation = ThreadPool.RegisterWaitForSingleObject(_openWindow,
            (_, _) => dispatcher.TryEnqueue(() => { if (!_shuttingDown) _main?.ShowMain(); }), null, Timeout.Infinite, false);
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        _activation?.Unregister(null);
        _tray?.Dispose();
        _main?.AllowCloseAndDispose();
        _mutex?.Dispose();
        _openWindow?.Dispose();
        Exit();
    }
}
