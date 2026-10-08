using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shared;
using Wpf = System.Windows.Controls;
using Brush = System.Windows.Media.Brush;

namespace App.Game;

public partial class FlightGameView : Wpf.UserControl, IDisposable
{
    private readonly CrashGame? _game;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private IReadOnlyList<FlightResult>? _lastHistory;
    private FlightPhase _lastPhase = (FlightPhase)(-1);
    private string? _error;
    private bool _disposed;
    private readonly Dictionary<string, Brush> _inks = new();

    public FlightGameView() : this(null) { }

    internal FlightGameView(CrashGame? game)
    {
        InitializeComponent();
        try
        {
            _game = game ?? new CrashGame(new FlightWallet(FlightWallet.DefaultPath, DateTime.Now));
            Scene.Game = _game;
            BetInput.Text = Coins(_game.Wallet.LastBet);
            if (_game.Wallet.LastAutoCashout > 0)
            {
                AutoInput.Text = _game.Wallet.LastAutoCashout.ToString("0.00", CultureInfo.CurrentCulture);
                AutoCheck.IsChecked = true;
            }
        }
        catch (Exception ex) when (IsWalletError(ex))
        {
            _error = "Wallet unavailable: " + ex.Message;
        }
        _tick.Tick += Tick;
        _tick.Start();
        PreviewKeyDown += OnGameKey;
        IsVisibleChanged += (_, _) => { if (IsVisible && !_disposed) RefreshView(); };
        RefreshView();
    }

    private static bool IsWalletError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException;
    private static string Coins(decimal amount) => amount.ToString("0.##", CultureInfo.CurrentCulture);
    private Brush Ink(string hex)
    {
        if (!_inks.TryGetValue(hex, out var brush))
        {
            brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            _inks.Add(hex, brush);
        }
        return brush;
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_disposed || _game == null || _error != null) return;
        try
        {
            _game.Update();
            // Also runs while hidden: a hidden window doesn't freeze a live bet.
            _game.Wallet.RefreshDailyGrant(DateTime.Now);
            _tick.Interval = TimeSpan.FromMilliseconds(_game.Phase is FlightPhase.Countdown or FlightPhase.Flying ? 50 : 1000);
            if (IsVisible) RefreshView();
        }
        catch (Exception ex) when (IsWalletError(ex))
        {
            _error = "Could not save coins: " + ex.Message;
            RefreshView();
        }
    }

    private void RefreshView()
    {
        if (_game == null || _error != null)
        {
            ActionButton.IsEnabled = false;
            BetControls.IsEnabled = AutoControls.IsEnabled = false;
            MessageText.Text = _error;
            MessageText.Foreground = Ink("#FF9BAC");
            return;
        }
        var g = _game;
        BalanceText.Text = $"● {Coins(g.Wallet.Balance)}";
        BalanceText.ToolTip = $"100 coins daily at 03:00. Next grant: {LogicalDay.NextResetLocal(DateTime.Now):ddd HH:mm}. Coins carry over.";
        bool active = g.Phase is FlightPhase.Countdown or FlightPhase.Flying;
        BetControls.IsEnabled = AutoControls.IsEnabled = !active;
        ActionButton.IsEnabled = g.HasLiveBet || !active && g.Wallet.Balance >= 1 &&
            (g.Phase != FlightPhase.Crashed || g.CrashAge >= 0.8);
        MultiplierText.Text = g.Phase == FlightPhase.Countdown ? $"{Math.Ceiling(g.CountdownLeft):0}" : $"{g.Multiplier:0.00}×";
        MultiplierText.Foreground = Ink(g.Phase == FlightPhase.Crashed ? "#FF7287" : "#F1F5FF");
        PhaseText.Text = g.Phase switch
        {
            FlightPhase.Ready => "READY FOR TAKEOFF",
            FlightPhase.Countdown => "TAKEOFF IN",
            FlightPhase.Flying => g.CashedAt > 0 ? "CASHED OUT · WATCH THE FLIGHT" : "CLIMBING",
            _ => g.Multiplier == CrashGame.MaximumMultiplier ? "MAXIMUM ALTITUDE" : "FLEW AWAY",
        };
        OutcomeText.Text = g.CashedAt > 0 ? $"+{Coins(g.Payout)} coins at {g.CashedAt:0.00}×"
            : g.Phase == FlightPhase.Crashed ? $"Lost {Coins(g.Bet)} coins · next flight, fresh sky"
            : g.Phase == FlightPhase.Ready ? "How far will you fly?" : $"{Coins(g.Bet)} coins on board";
        OutcomeText.Foreground = Ink(g.CashedAt > 0 ? "#6FE2B6" : "#AEC1E3");
        ActionButton.Background = Ink(g.HasLiveBet ? "#27BA89" : "#EF4C68");
        ActionButton.Foreground = Ink(g.HasLiveBet ? "#072C23" : "#FFFFFF");
        ActionButton.Content = g.Phase switch
        {
            FlightPhase.Countdown => "ON THE RUNWAY…",
            FlightPhase.Flying when g.HasLiveBet => $"CASH OUT  ·  {Coins(decimal.Floor(g.Bet * g.Multiplier * 100) / 100)} COINS",
            FlightPhase.Flying => "CASHED OUT  ✓",
            FlightPhase.Crashed => "FLY AGAIN",
            _ => "TAKE OFF",
        };
        if (_lastPhase != g.Phase)
        {
            _lastPhase = g.Phase;
            MessageText.Foreground = Ink("#98A8C3");
            MessageText.Text = "Cash out before the plane flies away. 100 coins daily · 03:00.";
        }
        if (g.Wallet.Balance < 1 && !active)
            MessageText.Text = $"Out of coins. Your next 100 arrive {LogicalDay.NextResetLocal(DateTime.Now):ddd 'at' HH:mm}.";
        // Rebuild only when a flight finishes, not on every animation frame.
        if (!ReferenceEquals(_lastHistory, g.Wallet.History))
        {
            _lastHistory = g.Wallet.History;
            HistoryPanel.Children.Clear();
            if (_lastHistory.Count == 0)
                HistoryPanel.Children.Add(new Wpf.TextBlock { Text = "YOUR FLIGHT LOG STARTS HERE", FontSize = 9, Foreground = Ink("#7389AD") });
            foreach (var result in g.Wallet.History.Take(5))
            {
                HistoryPanel.Children.Add(new Wpf.Border
                {
                    Background = Ink(result.CrashAt >= 2 ? "#243C47" : "#302840"),
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 5, 0),
                    ToolTip = result.CashedAt > 0 ? $"Bet {Coins(result.Bet)} · cashed out at {result.CashedAt:0.00}× · returned {Coins(result.Payout)}"
                        : $"Bet {Coins(result.Bet)} · lost",
                    Child = new Wpf.TextBlock { Text = $"{result.CrashAt:0.00}×", FontSize = 10, Foreground = Ink(result.CrashAt >= 2 ? "#72DBC6" : "#C1A3F1") },
                });
            }
        }
    }

    private void ActionClick(object sender, RoutedEventArgs e) => Act();

    private void Act()
    {
        if (_game == null || _error != null) return;
        try
        {
            if (_game.Phase == FlightPhase.Flying)
                _game.CashOut();
            else if (_game.Phase != FlightPhase.Countdown)
            {
                if (_game.Phase == FlightPhase.Crashed && _game.CrashAge < 0.8) return;
                if (!TryAmount(BetInput.Text, out decimal bet) || bet < 1 || bet > _game.Wallet.Balance || decimal.Round(bet, 2) != bet)
                {
                    ShowInputError("Bet at least 1 coin, up to your balance (max. 2 decimals).", BetInput);
                    return;
                }
                decimal automatic = 0;
                if (AutoCheck.IsChecked == true && (!TryAmount(AutoInput.Text, out automatic) || automatic < 1.01m || automatic > 100 || decimal.Round(automatic, 2) != automatic))
                {
                    ShowInputError("Auto cash-out must be 1.01× to 100× (max. 2 decimals).", AutoInput);
                    return;
                }
                _game.Start(bet, automatic, DateTime.Now);
                _tick.Interval = TimeSpan.FromMilliseconds(50);
                ActionButton.Focus();
            }
            RefreshView();
        }
        catch (Exception ex) when (IsWalletError(ex))
        {
            _error = "Could not save coins: " + ex.Message;
            RefreshView();
        }
    }

    private void ShowInputError(string text, Wpf.TextBox input)
    {
        MessageText.Text = text;
        MessageText.Foreground = Ink("#FF9BAC");
        input.Focus();
        input.SelectAll();
    }

    private void OnGameKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.OriginalSource is not Wpf.TextBox && _game?.HasLiveBet == true)
        {
            // Handle the preview event so the same key cannot click twice.
            e.Handled = true;
            if (!e.IsRepeat) Act();
        }
    }

    private static bool TryAmount(string text, out decimal amount) => decimal.TryParse(text,
        NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
        CultureInfo.CurrentCulture, out amount);

    private void DecreaseBet(object sender, RoutedEventArgs e) => AdjustBet(0.5m);
    private void IncreaseBet(object sender, RoutedEventArgs e) => AdjustBet(2);
    private void AdjustBet(decimal scale)
    {
        if (_game != null && TryAmount(BetInput.Text, out var amount))
            BetInput.Text = Coins(Math.Max(1, Math.Min(_game.Wallet.Balance, decimal.Floor(amount * scale * 100) / 100)));
    }
    private void PresetBet(object sender, RoutedEventArgs e)
    {
        if (_game != null && sender is Wpf.Button button)
            BetInput.Text = Coins(Math.Max(1, Math.Min(_game.Wallet.Balance, decimal.Parse((string)button.Tag, CultureInfo.InvariantCulture))));
    }
    private void AutoChanged(object sender, RoutedEventArgs e)
    {
        if (AutoInput != null) AutoInput.IsEnabled = AutoCheck.IsChecked == true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tick.Stop();
        _tick.Tick -= Tick;
        Scene.Dispose();
    }
}
