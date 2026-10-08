namespace App;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        // Single instance: if tray already runs, just open the window.
        using var mutex = new Mutex(true, @"Local\SteamTimerApp", out bool first);
        using var openWindow = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SteamTimerApp.Open");
        if (!first)
        {
            openWindow.Set();
            return;
        }
        using var tray = new TrayContext();
        tray.ShowMain();
        using var dispatcher = new Control();
        dispatcher.CreateControl();
        var activation = ThreadPool.RegisterWaitForSingleObject(openWindow, (_, _) =>
        {
            try { dispatcher.BeginInvoke(tray.ShowMain); }
            catch (InvalidOperationException) { } // Application is shutting down.
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        try
        {
            Application.Run(tray);
        }
        finally { activation.Unregister(null); }
    }
}
