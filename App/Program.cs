namespace App;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        // Single instance: if tray already runs, just open the window.
        using var mutex = new Mutex(true, @"Global\SteamTimerApp", out bool first);
        if (!first)
        {
            Application.Run(new MainForm());
            return;
        }
        var tray = new TrayContext();
        var form = new MainForm();
        form.Show();
        Application.Run(tray);
    }
}
