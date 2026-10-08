using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using App;
using App.Game;
using Microsoft.Extensions.Logging.Abstractions;
using Service;
using Shared;
using Wpf = System.Windows.Controls;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Tests;

public sealed class FlightViewTests
{
    [Fact]
    public void NativePanelRendersAndControlsPlayACompleteFlight()
    {
        RunSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "SteamTimerTests", Guid.NewGuid().ToString("N"));
            try
            {
                double clock = 0;
                var wallet = new FlightWallet(Path.Combine(directory, "wallet.json"), DateTime.Now);
                var game = new CrashGame(wallet, () => clock, () => 4);
                using var view = new FlightGameView(game) { Width = 360, Height = FlightGameView.PanelHeight };
                var window = new Window { Content = view, Width = 390, Height = FlightGameView.PanelHeight + 42, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                try
                {
                    window.Show();
                    Pump();
                    CaptureView(view, "flight-boarding.png", 1);
                    var action = (Wpf.Button)view.FindName("ActionButton");
                    var bet = (Wpf.TextBox)view.FindName("BetInput");
                    var outcome = (Wpf.TextBlock)view.FindName("OutcomeText");
                    var multiplier = (Wpf.TextBlock)view.FindName("MultiplierText");
                    Assert.Equal("5s", multiplier.Text);
                    Assert.Empty(outcome.Text);
                    bet.Text = "999";
                    Click(action);
                    Assert.Equal(FlightPhase.Boarding, game.Phase);
                    Assert.Contains("Bet at least", ((Wpf.TextBlock)view.FindName("MessageText")).Text);
                    bet.Text = "10";
                    ((Wpf.CheckBox)view.FindName("AutoCheck")).IsChecked = true;
                    ((Wpf.TextBox)view.FindName("AutoInput")).Text = "1.00";
                    Click(action);
                    Assert.Equal(FlightPhase.Boarding, game.Phase);
                    Assert.Equal(100, wallet.Balance);
                    Assert.Contains("Auto cash-out", ((Wpf.TextBlock)view.FindName("MessageText")).Text);
                    ((Wpf.TextBox)view.FindName("AutoInput")).Text = "2.00";
                    Click(action);
                    Assert.Equal(FlightPhase.Boarding, game.Phase);
                    Assert.Contains("ON BOARD", (string)action.Content);
                    Assert.False(action.IsEnabled);
                    CaptureView(view, "flight-countdown.png", 1);
                    clock = CrashGame.BoardingSeconds + CrashGame.SecondsFor(1.70m) + 0.001;
                    Pump();
                    Assert.Equal(FlightPhase.Flying, game.Phase);
                    Assert.Equal($"CASH OUT  ·  {17m:0.00} COINS", (string)action.Content);
                    Assert.Equal(FontNumeralAlignment.Tabular, action.GetValue(Typography.NumeralAlignmentProperty));
                    CaptureView(view, "flight-flying.png", 1);
                    CaptureView(view, "flight-high-dpi.png", 1.5);
                    clock = CrashGame.BoardingSeconds + CrashGame.SecondsFor(2.1m);
                    Pump();
                    Assert.Equal(2, game.CashedAt);
                    Assert.Equal(110, wallet.Balance);
                    CaptureView(view, "flight-cashed-out.png", 1);
                    window.Hide();
                    double firstCrashTime = CrashGame.BoardingSeconds + CrashGame.SecondsFor(4);
                    clock = firstCrashTime + 0.01;
                    Pump(300);
                    Assert.Equal(FlightPhase.Crashed, game.Phase);
                    window.Show();
                    Pump();
                    Assert.Contains("NEXT BOARDING", (string)action.Content);
                    CaptureView(view, "flight-crashed.png", 1);
                    clock = firstCrashTime + 4.99;
                    Pump();
                    Assert.Equal(FlightPhase.Crashed, game.Phase);
                    Assert.Equal($"{4m:0.00}×", multiplier.Text);

                    // A second flight verifies the real keyboard cash-out path.
                    clock = firstCrashTime + 5;
                    Pump();
                    Assert.Equal(FlightPhase.Boarding, game.Phase);
                    Assert.Equal("BOARD THIS FLIGHT", action.Content);
                    Assert.Equal(5d, game.BoardingLeft, 6);
                    Assert.Equal("5s", multiplier.Text);
                    Assert.Empty(outcome.Text);
                    ((Wpf.CheckBox)view.FindName("AutoCheck")).IsChecked = false;
                    bet.Text = 1.01m.ToString(System.Globalization.CultureInfo.CurrentCulture);
                    Click(action);
                    double secondBoarding = clock;
                    clock = secondBoarding + CrashGame.BoardingSeconds + CrashGame.SecondsFor(1.5m) + 0.001;
                    Pump();
                    Assert.Equal($"CASH OUT  ·  {1.52m:0.00} COINS", (string)action.Content);
                    var space = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(view)!, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    view.RaiseEvent(space);
                    Assert.True(space.Handled);
                    Assert.Equal(1.5m, game.CashedAt);
                    Assert.Equal(110.51m, wallet.Balance);
                    Assert.Contains($"+{1.52m:0.00}", outcome.Text);
                    clock = secondBoarding + CrashGame.BoardingSeconds + CrashGame.SecondsFor(4) + 0.01;
                    Pump();
                    Assert.Equal(2, wallet.History.Count);

                    // Do nothing during boarding: the next plane still launches.
                    clock += CrashGame.ResultSeconds;
                    Pump();
                    double spectatorBoarding = clock;
                    Assert.False(game.HasBet);
                    Assert.Empty(outcome.Text);
                    clock += CrashGame.BoardingSeconds + 1;
                    Pump();
                    Assert.Equal(FlightPhase.Flying, game.Phase);
                    Assert.Equal("WATCHING THIS FLIGHT", action.Content);
                    Assert.False(action.IsEnabled);
                    Assert.Empty(outcome.Text);
                    CaptureView(view, "flight-spectating.png", 1);
                    clock = spectatorBoarding + CrashGame.BoardingSeconds + CrashGame.SecondsFor(4) + 0.01;
                    Pump();
                    Assert.Equal(FlightPhase.Crashed, game.Phase);
                    Assert.Empty(outcome.Text);
                    Assert.Equal(0, wallet.History[0].Bet);
                    Assert.Equal(110.51m, wallet.Balance);
                }
                finally { window.Close(); }

                // Test the actual WinForms/WPF embedding and short-window scroll.
                using var form = new MainForm(new CrashGame(wallet, () => clock, () => 5));
                form.Show();
                form.UpdateFromStatus(new PipeResponse
                {
                    Ok = true, SupportsCoinExchange = true, ServerNowUtc = DateTime.UtcNow,
                    LogicalDay = LogicalDay.ForLocal(DateTime.Now), BudgetSeconds = 5400,
                    BaseBudgetSeconds = 5400, RemainingSeconds = 5400,
                    DotaBudgetSeconds = 32400, DotaRemainingSeconds = 32400,
                });
                Pump(600);
                var host = Descendants(form).OfType<System.Windows.Forms.Integration.ElementHost>().Single();
                Assert.True(host.Visible);
                Assert.True(host.Width >= 360);
                Assert.True(form.Width >= 416);
                CaptureForm(form, "steam-timer-flight.png");
                form.ClientSize = new System.Drawing.Size(form.ClientSize.Width, 460);
                form.ScrollControlIntoView(host);
                Pump();
                Assert.True(form.VerticalScroll.Visible);
                Assert.True(host.PointToScreen(System.Drawing.Point.Empty).Y < form.PointToScreen(new System.Drawing.Point(0, form.ClientSize.Height)).Y);
                CaptureForm(form, "steam-timer-short-window.png");
                // Dispose bypasses the normal X-to-tray behavior for this test.
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
    }

    [Fact]
    public void ExchangeButtonsUpdateCoinsAndBudgetWithoutBlockingTheFlight()
    {
        RunSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "SteamTimerTests", Guid.NewGuid().ToString("N"));
            try
            {
                double clock = 0;
                var wallet = new FlightWallet(Path.Combine(directory, "wallet.json"), DateTime.Now);
                var game = new CrashGame(wallet, () => clock, () => 4);
                var engine = new TimerEngine(NullLogger<TimerEngine>.Instance, new StateStore(Path.Combine(directory, "budget.json")));
                var reply = new TaskCompletionSource<PipeResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
                PipeRequest? sent = null;
                int calls = 0;
                using var view = new FlightGameView(game, request => { sent = request; calls++; return reply.Task; })
                    { Width = 360, Height = FlightGameView.PanelHeight };
                var window = new Window { Content = view, Width = 390, Height = FlightGameView.PanelHeight + 42, ShowInTaskbar = false };
                try
                {
                    window.Show();
                    Pump();
                    var sell = (Wpf.Button)view.FindName("SellCoinsButton");
                    var buy = (Wpf.Button)view.FindName("BuyCoinsButton");
                    Assert.False(sell.IsEnabled);
                    Assert.False(buy.IsEnabled);
                    view.SetBudgetStatus(new PipeResponse { Ok = true }); // Older installed service.
                    Assert.False(sell.IsEnabled);
                    PipeResponse? changed = null;
                    view.BudgetChanged += response => changed = response;
                    view.SetBudgetStatus(engine.Handle(new PipeRequest { Cmd = "status" }));
                    Assert.True(sell.IsEnabled);
                    Assert.True(buy.IsEnabled);
                    Click(sell);
                    Assert.Equal(99, wallet.Balance);
                    Assert.NotNull(wallet.PendingExchange);
                    Assert.False(sell.IsEnabled);
                    Assert.False(buy.IsEnabled);
                    Click(sell);
                    Assert.Equal(1, calls);
                    clock = 6;
                    Pump();
                    Assert.Equal(FlightPhase.Flying, game.Phase);
                    reply.SetResult(engine.Handle(sent));
                    Pump();
                    Assert.Null(wallet.PendingExchange);
                    Assert.Equal(60, changed!.CoinBudgetSeconds);
                    Assert.Equal(5460, changed.RemainingSeconds);
                    Assert.Contains("added 1m", ((Wpf.TextBlock)view.FindName("ExchangeMessageText")).Text);

                    reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    Click(buy);
                    Assert.Equal(2, calls);
                    Assert.Equal(99, wallet.Balance); // Coins arrive only with the saved service receipt.
                    reply.SetResult(engine.Handle(sent));
                    Pump();
                    Assert.Equal(109, wallet.Balance);
                    Assert.Equal(-600, changed!.CoinBudgetSeconds);
                    Assert.Equal(4800, changed.RemainingSeconds);
                    Assert.Contains("109", ((Wpf.TextBlock)view.FindName("BalanceText")).Text);
                    CaptureView(view, "flight-exchange.png", 1);

                    view.SetBudgetStatus(new PipeResponse
                    {
                        Ok = true, SupportsCoinExchange = true, LogicalDay = LogicalDay.ForLocal(DateTime.Now), RemainingSeconds = 600,
                    });
                    Assert.False(buy.IsEnabled);
                    Assert.True(sell.IsEnabled);
                    view.SetBudgetStatus(null);
                    Assert.False(sell.IsEnabled);
                    Assert.False(buy.IsEnabled);
                }
                finally { window.Close(); }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
    }

    [Fact]
    public void ALostExchangeReplyIsRecoveredByTheNextViewWithoutAnotherCharge()
    {
        RunSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "SteamTimerTests", Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(directory, "wallet.json");
                var wallet = new FlightWallet(path, DateTime.Now);
                var engine = new TimerEngine(NullLogger<TimerEngine>.Instance, new StateStore(Path.Combine(directory, "budget.json")));
                using (var view = new FlightGameView(new CrashGame(wallet, () => 0, () => 4), request =>
                {
                    Assert.True(engine.Handle(request).Ok);
                    return Task.FromResult<PipeResponse?>(null); // Applied, but the reply was lost.
                }))
                {
                    view.SetBudgetStatus(engine.Handle(new PipeRequest { Cmd = "status" }));
                    Click((Wpf.Button)view.FindName("SellCoinsButton"));
                    Pump();
                    Assert.Equal(99, wallet.Balance);
                    Assert.NotNull(wallet.PendingExchange);
                    Assert.Contains("pending", ((Wpf.TextBlock)view.FindName("ExchangeMessageText")).Text);
                }
                var reopened = new FlightWallet(path, DateTime.Now);
                using var recovered = new FlightGameView(new CrashGame(reopened, () => 0, () => 4),
                    request => Task.FromResult<PipeResponse?>(engine.Handle(request)));
                Pump();
                Assert.Null(reopened.PendingExchange);
                Assert.Equal(99, reopened.Balance);
                Assert.Equal(60, new StateStore(Path.Combine(directory, "budget.json")).Load()!.CoinBudgetSeconds);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoMinutesWithoutFocusStopsModelTicksAndRenderingUntilFocusReturns(bool hidden)
    {
        RunSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "SteamTimerTests", Guid.NewGuid().ToString("N"));
            try
            {
                double clock = 0;
                DateTime idleNow = DateTime.UtcNow;
                var wallet = new FlightWallet(Path.Combine(directory, "wallet.json"), DateTime.Now);
                var game = new CrashGame(wallet, () => clock, () => 4);
                using var view = new FlightGameView(game, idleNow: () => idleNow)
                    { Width = 360, Height = FlightGameView.PanelHeight };
                var window = new Window { Content = view, Width = 390, Height = FlightGameView.PanelHeight + 42, ShowInTaskbar = false };
                try
                {
                    window.Show();
                    Pump();
                    var scene = (FlightScene)view.FindName("Scene");
                    Assert.True(scene.IsRendering);
                    view.SetAppFocused(false);
                    if (hidden) window.Hide();
                    idleNow = idleNow.AddSeconds(119);
                    Pump(300);
                    Assert.False(game.IsPaused);
                    Assert.True(view.IsTicking);
                    Assert.Equal(!hidden, scene.IsRendering);
                    idleNow = idleNow.AddSeconds(1);
                    Pump(300);
                    Assert.True(game.IsPaused);
                    Assert.False(view.IsTicking);
                    Assert.False(scene.IsRendering);
                    Assert.Equal("PAUSED", ((Wpf.Button)view.FindName("ActionButton")).Content);
                    clock += 3600;
                    idleNow = idleNow.AddHours(1);
                    Pump();
                    Assert.Equal(5d, game.BoardingLeft, 6);
                    Assert.Empty(wallet.History);
                    if (hidden) window.Show();
                    view.SetAppFocused(true);
                    Pump();
                    Assert.False(game.IsPaused);
                    Assert.True(view.IsTicking);
                    Assert.True(scene.IsRendering);
                    Assert.Equal("5s", ((Wpf.TextBlock)view.FindName("MultiplierText")).Text);
                    view.SetAppFocused(false);
                    idleNow = idleNow.AddSeconds(119);
                    Pump();
                    Assert.False(game.IsPaused); // Returning resets the full grace period.
                }
                finally { window.Close(); }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
    }

    private static IEnumerable<Forms.Control> Descendants(Forms.Control control)
        => control.Controls.Cast<Forms.Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static void Click(Wpf.Button button) => button.RaiseEvent(new RoutedEventArgs(Wpf.Button.ClickEvent));

    private static void Pump(int milliseconds = 160)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Forms.Application.DoEvents();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static void CaptureView(FrameworkElement view, string filename, double scale)
    {
        view.UpdateLayout();
        var image = new RenderTargetBitmap((int)(view.ActualWidth * scale), (int)(view.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(view);
        byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        Assert.True(pixels.Distinct().Count() > 100, "The graphics panel must render actual imagery.");
        var evidence = Environment.GetEnvironmentVariable("STEAMTIMER_TEST_SCREENSHOTS");
        if (evidence == null) return;
        Directory.CreateDirectory(evidence);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(evidence, filename));
        encoder.Save(file);
    }

    private static void CaptureForm(Forms.Form form, string filename)
    {
        var evidence = Environment.GetEnvironmentVariable("STEAMTIMER_TEST_SCREENSHOTS");
        if (evidence == null) return;
        using var image = new System.Drawing.Bitmap(form.Width, form.Height);
        // Capture this form, not whichever app happens to be in the foreground.
        form.DrawToBitmap(image, new System.Drawing.Rectangle(System.Drawing.Point.Empty, form.Size));
        using var graphics = System.Drawing.Graphics.FromImage(image);
        foreach (var host in Descendants(form).OfType<System.Windows.Forms.Integration.ElementHost>())
        {
            if (host.Child is not FrameworkElement child) continue;
            var rendered = new RenderTargetBitmap(host.ClientSize.Width, host.ClientSize.Height,
                form.DeviceDpi, form.DeviceDpi, PixelFormats.Pbgra32);
            rendered.Render(child);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rendered));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            using var panel = System.Drawing.Image.FromStream(stream);
            var position = host.PointToScreen(System.Drawing.Point.Empty);
            graphics.DrawImageUnscaled(panel, position.X - form.Left, position.Y - form.Top);
        }
        image.Save(Path.Combine(evidence, filename), System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The UI test timed out.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
