using System.Globalization;
using System.Text.Json;
using Shared;

namespace App.Game;

internal sealed record FlightResult(decimal CrashAt, decimal Bet, decimal CashedAt, decimal Payout);
internal sealed record FlightTicket(Guid Id, decimal Bet, decimal CrashAt, decimal AutoCashout,
    decimal CashedAt = 0, decimal Payout = 0);

internal sealed record FlightWalletState
{
    public int Version { get; init; } = 1;
    public decimal Balance { get; init; }
    // New grants store the week's Monday; legacy daily dates count for their week.
    public string LastGrantDay { get; init; } = "";
    public decimal LastBet { get; init; } = 10;
    public decimal LastAutoCashout { get; init; }
    public FlightTicket? Pending { get; init; }
    public List<FlightResult> History { get; init; } = [];
    public Guid ExchangeWalletId { get; init; } = Guid.NewGuid();
    public long ExchangeNumber { get; init; }
    public CoinExchangeRequest? PendingExchange { get; init; }
}

// The service owns time; this wallet owns coins and journals their exchanges.
// A stake is durably deducted BEFORE
// takeoff; an interrupted flight stays paid/lost rather than refunding a restart.
internal sealed class FlightWallet
{
    public const decimal WeeklyCoins = 30;
    public const decimal MaximumBalance = 1_000_000_000_000m;
    private readonly string _path;
    private FlightWalletState _state;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public decimal Balance => _state.Balance;
    public decimal LastBet => _state.LastBet;
    public decimal LastAutoCashout => _state.LastAutoCashout;
    public IReadOnlyList<FlightResult> History => _state.History;
    public CoinExchangeRequest? PendingExchange => _state.PendingExchange;
    // Preserve room for a pending purchase or refund, even if a flight pays out.
    private decimal BalanceCap => MaximumBalance - (_state.PendingExchange is { } exchange
        ? exchange.CoinCount : 0);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamTimer", "flight-wallet.json");

    public FlightWallet(string path, DateTime localNow)
    {
        _path = path;
        if (File.Exists(path))
        {
            _state = JsonSerializer.Deserialize<FlightWalletState>(File.ReadAllText(path))
                ?? throw new InvalidDataException("The coin wallet is empty.");
            Validate(_state);
        }
        else
        {
            _state = new FlightWalletState();
        }

        if (_state.Pending is { } interrupted)
            Commit(WithResult(_state, interrupted));
        RefreshWeeklyGrant(localNow);
    }

    public bool RefreshWeeklyGrant(DateTime localNow)
    {
        string week = Limits.WeekStartForLogicalDay(LogicalDay.ForLocal(localNow));
        string lastWeek = Limits.WeekStartForLogicalDay(_state.LastGrantDay);
        // Same Monday 03:00 schedule as Dota. A legacy daily grant already counts
        // for its week; missed weeks and clock rollbacks never stack allowances.
        if (string.CompareOrdinal(week, lastWeek) <= 0) return false;
        Commit(_state with
        {
            Balance = Math.Min(BalanceCap, _state.Balance + WeeklyCoins),
            LastGrantDay = week,
        });
        return true;
    }

    public FlightTicket PlaceBet(decimal bet, decimal crashAt, decimal autoCashout)
    {
        if (_state.Pending != null) throw new InvalidOperationException("A flight is already in progress.");
        if (bet < 1 || bet > Balance || decimal.Round(bet, 2) != bet)
            throw new ArgumentOutOfRangeException(nameof(bet), "Choose a bet from 1 coin up to your balance (two decimal places).");
        if (crashAt < 1 || crashAt > CrashGame.MaximumMultiplier || decimal.Round(crashAt, 2) != crashAt)
            throw new ArgumentOutOfRangeException(nameof(crashAt));
        if (autoCashout != 0 && (autoCashout < 1.01m || autoCashout > CrashGame.MaximumMultiplier || decimal.Round(autoCashout, 2) != autoCashout))
            throw new ArgumentOutOfRangeException(nameof(autoCashout));
        var ticket = new FlightTicket(Guid.NewGuid(), bet, crashAt, autoCashout);
        Commit(_state with { Balance = Balance - bet, Pending = ticket, LastBet = bet, LastAutoCashout = autoCashout });
        return ticket;
    }

    public decimal CashOut(Guid id, decimal multiplier)
    {
        var ticket = RequireTicket(id);
        if (ticket.CashedAt > 0) return ticket.Payout;
        if (multiplier < 1 || multiplier >= ticket.CrashAt && !(multiplier == CrashGame.MaximumMultiplier && ticket.CrashAt == CrashGame.MaximumMultiplier))
            throw new InvalidOperationException("The plane has already flown away.");
        decimal payout = PayoutFor(ticket.Bet, multiplier);
        Commit(_state with
        {
            Balance = Math.Min(BalanceCap, Balance + payout),
            Pending = ticket with { CashedAt = multiplier, Payout = payout },
        });
        return payout;
    }

    public void Finish(Guid id) => Commit(WithResult(_state, RequireTicket(id)));

    public static decimal PayoutFor(decimal bet, decimal multiplier) => decimal.Ceiling(bet * multiplier * 100) / 100;

    public CoinExchangeRequest BeginExchange(bool buyCoins, DateTime localNow, int coins = 1)
    {
        if (PendingExchange != null) throw new InvalidOperationException("An exchange is still pending.");
        if (coins < 1 || buyCoins && coins != 1 || !buyCoins && coins > CoinExchangeRates.MaximumSaleCoins)
            throw new ArgumentOutOfRangeException(nameof(coins), "Buy one coin, or choose a positive whole-coin sale amount.");
        if (buyCoins && Balance > MaximumBalance - coins)
            throw new InvalidOperationException("The coin wallet is full.");
        if (!buyCoins && Balance < coins)
            throw new InvalidOperationException("Choose a sale amount up to your available coin balance.");
        var request = new CoinExchangeRequest(_state.ExchangeWalletId, checked(_state.ExchangeNumber + 1),
            LogicalDay.ForLocal(localNow), buyCoins, coins);
        Commit(_state with
        {
            Balance = Balance - (buyCoins ? 0 : coins),
            PendingExchange = request,
        });
        return request;
    }

    public void CompleteExchange(CoinExchangeReceipt receipt)
    {
        if (PendingExchange is not { } pending || pending != receipt.Request)
            throw new InvalidOperationException("The exchange receipt does not match the pending trade.");
        decimal credit = receipt.Applied && pending.BuyCoins || !receipt.Applied && !pending.BuyCoins ? pending.CoinCount : 0;
        Commit(_state with
        {
            Balance = Balance + credit,
            ExchangeNumber = pending.Number,
            PendingExchange = null,
        });
    }

    public void RecordSpectatorFlight(decimal crashAt)
    {
        if (_state.Pending != null) throw new InvalidOperationException("A bet still needs to be settled.");
        if (!ValidFlight(0, crashAt, 0, 0, allowSpectator: true))
            throw new ArgumentOutOfRangeException(nameof(crashAt));
        Commit(_state with
        {
            History = _state.History.Prepend(new FlightResult(crashAt, 0, 0, 0)).Take(8).ToList(),
        });
    }

    private FlightTicket RequireTicket(Guid id) => _state.Pending is { } ticket && ticket.Id == id
        ? ticket : throw new InvalidOperationException("This flight is no longer active.");

    private static FlightWalletState WithResult(FlightWalletState state, FlightTicket ticket) => state with
    {
        Pending = null,
        History = state.History.Prepend(new FlightResult(ticket.CrashAt, ticket.Bet, ticket.CashedAt, ticket.Payout)).Take(8).ToList(),
    };

    private void Commit(FlightWalletState next)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        string temp = _path + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, next, JsonOptions);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
            _state = next;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void Validate(FlightWalletState state)
    {
        if (state.Version != 1 || state.Balance < 0 || state.Balance > MaximumBalance ||
            decimal.Round(state.Balance, 2) != state.Balance || state.History == null || state.History.Count > 8 ||
            !DateTime.TryParseExact(state.LastGrantDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            state.LastBet < 1 || state.LastBet > MaximumBalance || decimal.Round(state.LastBet, 2) != state.LastBet ||
            state.LastAutoCashout != 0 && (state.LastAutoCashout < 1.01m || state.LastAutoCashout > CrashGame.MaximumMultiplier) ||
            state.History.Any(r => r == null || !ValidFlight(r.Bet, r.CrashAt, r.CashedAt, r.Payout, allowSpectator: true)) ||
            state.Pending is { } p && (!ValidFlight(p.Bet, p.CrashAt, p.CashedAt, p.Payout) ||
                p.AutoCashout != 0 && (p.AutoCashout < 1.01m || p.AutoCashout > CrashGame.MaximumMultiplier)) ||
            state.ExchangeWalletId == Guid.Empty || state.ExchangeNumber < 0 ||
            state.PendingExchange is { } exchange && (exchange.WalletId != state.ExchangeWalletId ||
                !exchange.HasValidAmount ||
                state.ExchangeNumber == long.MaxValue || exchange.Number != state.ExchangeNumber + 1 ||
                !DateTime.TryParseExact(exchange.LogicalDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                state.Balance > MaximumBalance - exchange.CoinCount))
            throw new InvalidDataException("The saved coin wallet is invalid. Its file has been preserved.");
    }

    private static bool ValidFlight(decimal bet, decimal crashAt, decimal cashedAt, decimal payout, bool allowSpectator = false)
        => (bet >= 1 || allowSpectator && bet == 0 && cashedAt == 0) && bet <= MaximumBalance && decimal.Round(bet, 2) == bet &&
           crashAt >= 1 && crashAt <= CrashGame.MaximumMultiplier && decimal.Round(crashAt, 2) == crashAt &&
           cashedAt >= 0 && cashedAt <= crashAt &&
           // Earlier versions rounded down. Preserve those saved results and
           // pending payouts; only new cash-outs use upward cent rounding.
           (cashedAt == 0 ? payout == 0 : payout == PayoutFor(bet, cashedAt) || payout == decimal.Floor(bet * cashedAt * 100) / 100);
}
