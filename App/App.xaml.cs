using System.Threading;
using System.Windows;

namespace App;

public partial class SteamTimerApp : System.Windows.Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _openWindow;
    private RegisteredWaitHandle? _activation;
    private TrayIcon? _tray;
    private MainWindow? _main;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\SteamTimerApp", out bool first);
        _openWindow = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SteamTimerApp.Open");
        if (!first)
        {
            try { _openWindow.Set(); } catch { }
            Shutdown();
            return;
        }

        _main = new MainWindow();
        _tray = new TrayIcon(
            showMain: () => Dispatcher.Invoke(() => _main.ShowMain()),
            getWindow: () => _main);
        _main.SetTray(_tray);
        _main.Show();

        _activation = ThreadPool.RegisterWaitForSingleObject(_openWindow,
            (_, _) =>
            {
                try { Dispatcher.Invoke(() => _main.ShowMain()); } catch { }
            }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try { _activation?.Unregister(null); } catch { }
        try { _main?.AllowCloseAndDispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        try { _openWindow?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
