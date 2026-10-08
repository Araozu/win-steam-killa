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
    internal const int PanelHeight = 514;
    private static readonly TimeSpan IdlePauseDelay = TimeSpan.FromMinutes(2);
    private readonly CrashGame? _game;
    private readonly Func<PipeRequest, Task<PipeResponse?>> _send;
    private readonly Func<DateTime> _idleNow;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private IReadOnlyList<FlightResult>? _lastHistory;
    private FlightPhase _lastPhase = (FlightPhase)(-1);
    private string? _error;
    private bool _disposed;
    private Guid _shownRoundId;
    private bool _boardingAction;
    private PipeResponse? _budgetStatus;
    private DateTime _budgetReceivedAt;
    private bool _exchangeBusy;
    private DateTime _exchangeRetryAt;
    private string? _exchangeMessage;
    private bool _appFocused = true;
    private DateTime _unfocusedAt;
    private readonly Dictionary<string, Brush> _inks = new();

    public event Action<PipeResponse>? BudgetChanged;
    internal bool IsTicking => _tick.IsEnabled;

    public FlightGameView() : this(null) { }

    internal FlightGameView(CrashGame? game, Func<PipeRequest, Task<PipeResponse?>>? send = null,
        Func<DateTime>? idleNow = null)
    {
        _send = send ?? (request => PipeClient.SendAsync(request));
        _idleNow = idleNow ?? (() => DateTime.UtcNow);
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
        IsVisibleChanged += (_, _) =>
        {
            if (_disposed) return;
            _tick.Interval = TimeSpan.FromMilliseconds(IsVisible ? 50 : 250);
            if (IsVisible) RefreshView();
        };
        RefreshView();
    }

    private static bool IsWalletError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException;
    private static string Coins(decimal amount) => amount.ToString("0.##", CultureInfo.CurrentCulture);
    private static string Gain(decimal amount) => amount.ToString("0.00", CultureInfo.CurrentCulture);
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
        if (_disposed) return;
        try
        {
            if (!_appFocused && _idleNow() - _unfocusedAt >= IdlePauseDelay)
            {
                _tick.Stop();
                Scene.SetPaused(true);
                _game?.Pause();
                RefreshView();
                return;
            }
            if (_game == null || _error != null) return;
            _game.Update();
            // Loop during the inactivity grace period, including while hidden.
            _game.Wallet.RefreshWeeklyGrant(DateTime.Now);
            if (_game.Wallet.PendingExchange != null && !_exchangeBusy && DateTime.UtcNow >= _exchangeRetryAt)
                _ = ResolveExchangeAsync();
            _tick.Interval = TimeSpan.FromMilliseconds(IsVisible ? 50 : 250);
            if (IsVisible) RefreshView();
        }
        catch (Exception ex) when (IsWalletError(ex))
        {
            _error = "Could not save coins: " + ex.Message;
            RefreshView();
        }
    }

    public void SetAppFocused(bool focused)
    {
        if (_disposed || _appFocused == focused) return;
        _appFocused = focused;
        if (!focused) _unfocusedAt = _idleNow();
        else
        {
            _game?.Resume();
            Scene.SetPaused(false);
            _lastPhase = (FlightPhase)(-1);
            _tick.Start();
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
            SellCoinsButton.IsEnabled = BuyCoinsButton.IsEnabled = false;
            SellCoinsInput.IsEnabled = false;
            return;
        }
        var g = _game;
        BalanceText.Text = $"● {Coins(g.Wallet.Balance)}";
        BalanceText.ToolTip = $"{FlightWallet.WeeklyCoins:0} coins weekly, Monday at 03:00. Next grant: {Limits.NextWeeklyResetLocal(DateTime.Now):ddd HH:mm}. Coins carry over.";
        bool newRound = _shownRoundId != g.RoundId;
        _shownRoundId = g.RoundId;
        _boardingAction = !g.IsPaused && g.Phase == FlightPhase.Boarding && !g.HasBet;
        BetControls.IsEnabled = AutoControls.IsEnabled = _boardingAction;
        ActionButton.IsEnabled = !g.IsPaused && (g.HasLiveBet || _boardingAction && g.Wallet.Balance >= 1);
        MultiplierText.Text = g.Phase == FlightPhase.Boarding ? $"{Math.Ceiling(g.BoardingLeft):0}s" : $"{g.Multiplier:0.00}×";
        MultiplierText.Foreground = Ink(g.Phase == FlightPhase.Crashed ? "#FF7287" : "#F1F5FF");
        PhaseText.Text = g.IsPaused ? "PAUSED · SWITCH BACK TO RESUME" : g.Phase switch
        {
            FlightPhase.Boarding => g.HasBet ? "YOU'RE ON BOARD · TAKEOFF IN" : "BOARDING OPEN · TAKEOFF IN",
            FlightPhase.Flying => g.CashedAt > 0 ? "CASHED OUT · WATCH THE FLIGHT" : g.HasBet ? "CLIMBING" : "CLIMBING · SPECTATING",
            _ => g.Multiplier == CrashGame.MaximumMultiplier ? "MAXIMUM ALTITUDE" : "FLEW AWAY",
        };
        OutcomeText.Text = g.CashedAt > 0 ? $"+{Gain(g.Payout)} coins at {g.CashedAt:0.00}×"
            : !g.HasBet ? string.Empty
            : g.Phase == FlightPhase.Crashed ? $"Lost {Coins(g.Bet)} coins · next flight soon"
            : $"{Coins(g.Bet)} coins on board";
        OutcomeText.Foreground = Ink(g.CashedAt > 0 ? "#6FE2B6" : "#AEC1E3");
        ActionButton.Background = Ink(g.HasLiveBet ? "#27BA89" : _boardingAction ? "#EF4C68" : "#33435D");
        ActionButton.Foreground = Ink(g.HasLiveBet ? "#072C23" : "#FFFFFF");
        ActionButton.Content = g.IsPaused ? "PAUSED" : g.Phase switch
        {
            FlightPhase.Boarding => g.HasBet ? $"ON BOARD  ·  {Coins(g.Bet)} COINS" : "BOARD THIS FLIGHT",
            FlightPhase.Flying when g.HasLiveBet => $"CASH OUT  ·  {Gain(FlightWallet.PayoutFor(g.Bet, g.Multiplier))} COINS",
            FlightPhase.Flying => g.CashedAt > 0 ? "CASHED OUT  ✓" : "WATCHING THIS FLIGHT",
            _ => $"NEXT BOARDING IN {Math.Ceiling(Math.Max(0, CrashGame.ResultSeconds - g.CrashAge)):0}s",
        };
        if (newRound || _lastPhase != g.Phase)
        {
            _lastPhase = g.Phase;
            MessageText.Foreground = Ink("#98A8C3");
            MessageText.Text = $"Flights run nonstop. Board during the {CrashGame.BoardingSeconds:0}-second countdown.";
        }
        if (g.Wallet.Balance < 1 && !g.HasLiveBet)
            MessageText.Text = $"Keep watching · next {FlightWallet.WeeklyCoins:0} coins {Limits.NextWeeklyResetLocal(DateTime.Now):ddd 'at' HH:mm}.";
        if (g.IsPaused)
            MessageText.Text = "Game paused after 2 minutes away. Return to this window to resume.";
        // Rebuild only when a flight finishes, not on every animation frame.
        if (!ReferenceEquals(_lastHistory, g.Wallet.History))
        {
            _lastHistory = g.Wallet.History;
            HistoryPanel.Children.Clear();
            if (_lastHistory.Count == 0)
                HistoryPanel.Children.Add(new Wpf.TextBlock { Text = "NONSTOP FLIGHTS · JOIN ANY ROUND", FontSize = 9, Foreground = Ink("#7389AD") });
            foreach (var result in g.Wallet.History.Take(5))
            {
                HistoryPanel.Children.Add(new Wpf.Border
                {
                    Background = Ink(result.CrashAt >= 2 ? "#243C47" : "#302840"),
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 5, 0),
                    ToolTip = result.Bet == 0 ? "Watched this flight · no coins bet"
                        : result.CashedAt > 0 ? $"Bet {Coins(result.Bet)} · cashed out at {result.CashedAt:0.00}× · returned {Gain(result.Payout)}"
                        : $"Bet {Coins(result.Bet)} · lost",
                    Child = new Wpf.TextBlock { Text = $"{result.CrashAt:0.00}×", FontSize = 10, Foreground = Ink(result.CrashAt >= 2 ? "#72DBC6" : "#C1A3F1") },
                });
            }
        }
        RefreshExchangeControls();
    }

    public void SetBudgetStatus(PipeResponse? status)
    {
        if (_disposed) return;
        if (!ReferenceEquals(status, _budgetStatus)) _budgetReceivedAt = DateTime.UtcNow;
        _budgetStatus = status;
        if (_game?.IsPaused != true) RefreshExchangeControls();
    }

    private void RefreshExchangeControls()
    {
        bool available = _game != null && !_game.IsPaused && _error == null && !_exchangeBusy && _game.Wallet.PendingExchange == null &&
            _budgetStatus?.SupportsCoinExchange == true && _budgetStatus.CoinExchangeVersion >= CoinExchangeRates.ProtocolVersion &&
            _budgetStatus.LogicalDay == LogicalDay.ForLocal(DateTime.Now);
        bool validSale = TrySaleCoins(out int saleCoins);
        SellCoinsInput.IsEnabled = _game != null && !_game.IsPaused && _error == null && !_exchangeBusy && _game.Wallet.PendingExchange == null;
        SellCoinsButton.Content = validSale ? $"SELL · +{saleCoins}m" : "SELL COINS";
        if (validSale)
        {
            string coins = $"{saleCoins} {(saleCoins == 1 ? "coin" : "coins")}";
            string time = $"{saleCoins} {(saleCoins == 1 ? "minute" : "minutes")}";
            SellCoinsButton.ToolTip = $"Sell {coins} to add {time} to today's Steam budget.";
            System.Windows.Automation.AutomationProperties.SetName(SellCoinsButton, $"Sell {coins} for {time} today");
        }
        double remaining = _budgetStatus?.RemainingSeconds ?? 0;
        if (_budgetStatus?.Running == true && !_budgetStatus.InCooldown)
            remaining = Math.Max(0, remaining - (DateTime.UtcNow - _budgetReceivedAt).TotalSeconds);
        SellCoinsButton.IsEnabled = available && validSale && _game!.Wallet.Balance >= saleCoins;
        BuyCoinsButton.IsEnabled = available && remaining >= CoinExchangeRates.PurchaseSeconds &&
            _game!.Wallet.Balance <= FlightWallet.MaximumBalance - CoinExchangeRates.BoughtCoins;
        ExchangeMessageText.Text = _game?.Wallet.PendingExchange != null
            ? _exchangeMessage ?? "Exchange pending · waiting for the service."
            : _exchangeMessage ?? (_budgetStatus == null ? "Connect to the SteamTimer service to trade coins."
                : !_budgetStatus.SupportsCoinExchange || _budgetStatus.CoinExchangeVersion < CoinExchangeRates.ProtocolVersion
                    ? "Update the SteamTimer service to trade coins."
                : !validSale ? "Enter a positive whole number of coins to sell."
                : $"Today: {Limits.FormatGain(TimeSpan.FromSeconds(remaining))} remaining · trades use today's budget.");
    }

    private bool TrySaleCoins(out int coins)
    {
        coins = 0;
        if (!TryAmount(SellCoinsInput.Text, out decimal amount) || amount < CoinExchangeRates.SoldCoins ||
            amount > CoinExchangeRates.MaximumSaleCoins || decimal.Truncate(amount) != amount) return false;
        coins = (int)amount;
        return true;
    }

    private void SaleAmountChanged(object sender, Wpf.TextChangedEventArgs e)
    {
        if (SellCoinsButton == null || BuyCoinsButton == null || ExchangeMessageText == null) return;
        _exchangeMessage = null;
        RefreshExchangeControls();
    }

    private async void SellCoinsClick(object sender, RoutedEventArgs e) => await ExchangeAsync(buyCoins: false);
    private async void BuyCoinsClick(object sender, RoutedEventArgs e) => await ExchangeAsync(buyCoins: true);

    private async Task ExchangeAsync(bool buyCoins)
    {
        if (_disposed || _game == null || _error != null || _exchangeBusy ||
            !(buyCoins ? BuyCoinsButton.IsEnabled : SellCoinsButton.IsEnabled)) return;
        int coins = 1;
        if (!buyCoins && !TrySaleCoins(out coins)) return;
        try
        {
            _game.Update();
            _game.Wallet.RefreshWeeklyGrant(DateTime.Now);
            _game.Wallet.BeginExchange(buyCoins, DateTime.Now, coins);
            _exchangeMessage = "Exchanging…";
            RefreshView();
            await ResolveExchangeAsync();
        }
        catch (Exception ex) when (IsWalletError(ex))
        {
            _error = "Could not save coins: " + ex.Message;
            RefreshView();
        }
        catch (InvalidOperationException ex)
        {
            _exchangeMessage = ex.Message;
            RefreshExchangeControls();
        }
    }

    private async Task ResolveExchangeAsync()
    {
        if (_disposed || _exchangeBusy || _game?.Wallet.PendingExchange is not { } pending) return;
        _exchangeBusy = true;
        RefreshExchangeControls();
        PipeResponse? response;
        try { response = await _send(new PipeRequest { Cmd = CoinExchangeRates.Command, Exchange = pending }).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { response = null; }
        if (_disposed) return; // The journal is recovered by the next app instance.
        // Explicit dispatcher ownership also works in a standalone WPF host,
        // where an input event need not have a SynchronizationContext installed.
        await Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (_disposed) return;
                if (response?.ExchangeReceipt is { } receipt && receipt.Request == pending)
                {
                    _game.Wallet.CompleteExchange(receipt);
                    string coins = $"{pending.CoinCount} {(pending.CoinCount == 1 ? "coin" : "coins")}";
                    string time = Limits.FormatGain(TimeSpan.FromSeconds(pending.TimeSeconds));
                    _exchangeMessage = receipt.Applied
                        ? pending.BuyCoins ? $"Bought {coins} · spent {time} of today's budget." : $"Sold {coins} · added {time} to today's budget."
                        : receipt.Error;
                    SetBudgetStatus(response);
                    BudgetChanged?.Invoke(response);
                }
                else _exchangeMessage = "Exchange pending · service unavailable. Retrying…";
            }
            catch (Exception ex) when (IsWalletError(ex)) { _error = "Could not save coins: " + ex.Message; }
            finally
            {
                _exchangeBusy = false;
                _exchangeRetryAt = DateTime.UtcNow.AddSeconds(5);
                if (!_disposed) RefreshView();
            }
        });
    }

    private void ActionClick(object sender, RoutedEventArgs e) => Act();

    private void Act()
    {
        if (_game == null || _game.IsPaused || _error != null) return;
        try
        {
            if (_boardingAction)
            {
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
                if (!_game.Board(bet, automatic, DateTime.Now, _shownRoundId))
                {
                    RefreshView();
                    MessageText.Text = "Boarding closed. Join during the next countdown.";
                    return;
                }
                _tick.Interval = TimeSpan.FromMilliseconds(50);
                ActionButton.Focus();
            }
            else _game.CashOut(_shownRoundId);
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
