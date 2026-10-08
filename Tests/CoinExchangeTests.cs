using App.Game;
using Microsoft.Extensions.Logging.Abstractions;
using Service;
using Shared;

namespace Tests;

public sealed class CoinExchangeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SteamTimerTests", Guid.NewGuid().ToString("N"));
    private DateTime _localNow = new(2026, 10, 8, 12, 0, 0);
    private string WalletPath => Path.Combine(_directory, "wallet.json");
    private string BudgetPath => Path.Combine(_directory, "budget.json");
    private StateStore Store => new(BudgetPath);
    private FlightWallet Wallet() => new(WalletPath, _localNow);
    private TimerEngine Engine() => new(NullLogger<TimerEngine>.Instance, Store, () => _localNow.ToUniversalTime());
    private PipeResponse Send(TimerEngine engine, CoinExchangeRequest request)
        => engine.Handle(new PipeRequest { Cmd = CoinExchangeRates.Command, Exchange = request });

    [Fact]
    public void SellingOneCoinAddsOneMinuteAndBuyingTenCostsElevenMinutes()
    {
        var wallet = Wallet();
        var engine = Engine();
        var sale = wallet.BeginExchange(false, _localNow);
        Assert.Equal(99, wallet.Balance);
        var sold = Send(engine, sale);
        Assert.True(sold.Ok);
        Assert.Equal(5460, sold.BudgetSeconds);
        Assert.Equal(60, sold.CoinBudgetSeconds);
        wallet.CompleteExchange(sold.ExchangeReceipt!);
        var bought = Send(engine, wallet.BeginExchange(true, _localNow));
        Assert.True(bought.Ok);
        wallet.CompleteExchange(bought.ExchangeReceipt!);
        Assert.Equal(109, wallet.Balance);
        Assert.Equal(4800, bought.RemainingSeconds);
        Assert.Equal(-600, bought.CoinBudgetSeconds);
        Assert.Equal(109, Wallet().Balance);
        Assert.Equal(-600, Store.Load()!.CoinBudgetSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostRepliesAndBothProcessesRestartingApplyAnExchangeOnlyOnce(bool buyCoins)
    {
        var wallet = Wallet();
        var request = wallet.BeginExchange(buyCoins, _localNow);
        var first = Send(Engine(), request); // Reply lost before the wallet saves it.
        var reopened = Wallet();
        Assert.Equal(request, reopened.PendingExchange);
        var retried = Send(Engine(), reopened.PendingExchange!);
        Assert.Equal(first.ExchangeReceipt, retried.ExchangeReceipt);
        Assert.Equal(first.CoinBudgetSeconds, retried.CoinBudgetSeconds);
        reopened.CompleteExchange(retried.ExchangeReceipt!);
        Assert.Null(Wallet().PendingExchange);
        Assert.Equal(buyCoins ? 110 : 99, Wallet().Balance);
        Assert.Throws<InvalidOperationException>(() => reopened.CompleteExchange(retried.ExchangeReceipt!));
        Assert.Equal(buyCoins ? 110 : 99, reopened.Balance);
    }

    [Fact]
    public void InsufficientRemainingTimeRejectsThePurchaseWithoutCreatingCoins()
    {
        var wallet = Wallet();
        SeedBudget(used: 4800);
        var response = Send(Engine(), wallet.BeginExchange(true, _localNow));
        Assert.False(response.Ok);
        Assert.False(response.ExchangeReceipt!.Applied);
        Assert.Contains("11 minutes", response.Error);
        wallet.CompleteExchange(response.ExchangeReceipt);
        Assert.Equal(100, Wallet().Balance);
        Assert.Equal(600, response.RemainingSeconds);
        Assert.Equal(0, Store.Load()!.CoinBudgetSeconds);
        Assert.Null(wallet.PendingExchange);
    }

    [Fact]
    public void ARejectedPurchaseStaysRejectedAfterMoreTimeIsAdded()
    {
        SeedBudget(used: 4800);
        var wallet = Wallet();
        var engine = Engine();
        var request = wallet.BeginExchange(true, _localNow);
        var rejected = Send(engine, request);
        var otherWallet = new CoinExchangeRequest(Guid.NewGuid(), 1, request.LogicalDay, false);
        Assert.True(Send(engine, otherWallet).Ok);
        Assert.True(Send(engine, otherWallet with { Number = 2 }).Ok);
        var replay = Send(engine, request);
        Assert.Equal(rejected.ExchangeReceipt, replay.ExchangeReceipt);
        Assert.False(replay.Ok);
        Assert.Equal(120, replay.CoinBudgetSeconds);
    }

    [Fact]
    public void UnmeteredActivePlayCannotBeSpentOnCoins()
    {
        SeedBudget(used: 4740, running: true, tickAge: 15);
        var response = Send(Engine(), Wallet().BeginExchange(true, _localNow));
        Assert.False(response.Ok);
        Assert.Equal(0, response.CoinBudgetSeconds);
    }

    [Fact]
    public void SellingCoinsReopensAnExhaustedBudgetWithoutChangingOtherLimits()
    {
        SeedBudget(used: 5400, cooldown: true);
        var before = Store.Load()!;
        before.KillNotified = true;
        Store.Save(before);
        var response = Send(Engine(), Wallet().BeginExchange(false, _localNow));
        Assert.True(response.Ok);
        Assert.False(response.Enforcing);
        Assert.Equal(60, response.RemainingSeconds);
        var after = Store.Load()!;
        Assert.False(after.KillNotified);
        Assert.Equal(before.UsedSeconds, after.UsedSeconds);
        Assert.Equal(before.SessionUsedSeconds, after.SessionUsedSeconds);
        Assert.Equal(before.CooldownUntilUtc, after.CooldownUntilUtc);
        Assert.Equal(before.DotaUsedSeconds, after.DotaUsedSeconds);
        Assert.Equal(before.BorrowCount, after.BorrowCount);
        Assert.Equal(before.AdvanceDeductionSeconds, after.AdvanceDeductionSeconds);
    }

    [Fact]
    public void ExchangeAppliesToAnOpenSessionWithoutRestartingIt()
    {
        SeedBudget(used: 100, running: true);
        var before = Store.Load()!;
        var response = Send(Engine(), Wallet().BeginExchange(true, _localNow));
        var after = Store.Load()!;
        Assert.True(response.Ok);
        Assert.True(after.Running);
        Assert.Equal(before.SessionStartUtc, after.SessionStartUtc);
        Assert.Equal(before.SessionUsedSeconds, after.SessionUsedSeconds);
        Assert.Equal(before.UsedSeconds, after.UsedSeconds);
        Assert.Equal(4640, response.RemainingSeconds);
    }

    [Fact]
    public void ATradeCrossingTheDailyResetRefundsReservedCoins()
    {
        var wallet = Wallet();
        var request = wallet.BeginExchange(false, _localNow);
        _localNow = _localNow.AddDays(1);
        var response = Send(Engine(), request);
        Assert.False(response.Ok);
        Assert.Equal(0, response.CoinBudgetSeconds);
        wallet.CompleteExchange(response.ExchangeReceipt!);
        Assert.Equal(100, wallet.Balance);
        wallet.RefreshDailyGrant(_localNow);
        Assert.Equal(200, wallet.Balance);
    }

    [Fact]
    public void AnAppliedTradeCanBeRecoveredAfterTheDayResetWithoutTradingAgain()
    {
        var wallet = Wallet();
        var request = wallet.BeginExchange(true, _localNow);
        Assert.True(Send(Engine(), request).Ok);
        _localNow = _localNow.AddDays(1);
        var reopened = Wallet();
        var response = Send(Engine(), request);
        Assert.True(response.Ok);
        Assert.Equal(0, response.CoinBudgetSeconds);
        Assert.Equal(5400 + 1800, response.BudgetSeconds);
        reopened.CompleteExchange(response.ExchangeReceipt!);
        Assert.Equal(210, Wallet().Balance);
    }

    [Fact]
    public void SpendingAnEntireBudgetCannotGenerateCarryoverAtReset()
    {
        SeedBudget();
        var state = Store.Load()!;
        state.BaseBudgetSeconds = 660;
        Store.Save(state);
        var response = Send(Engine(), Wallet().BeginExchange(true, _localNow));
        Assert.True(response.Ok);
        Assert.Equal(0, response.RemainingSeconds);
        Assert.True(response.Enforcing);
        _localNow = _localNow.AddDays(1);
        var next = Engine().Handle(new PipeRequest { Cmd = "status" });
        Assert.Equal(0, next.CarryInSeconds);
        Assert.Equal(5400, next.BudgetSeconds);
        Assert.Equal(0, next.CoinBudgetSeconds);
    }

    [Fact]
    public void FailedServiceSaveCannotPublishAnAdjustmentOrReceipt()
    {
        SeedBudget();
        var engine = Engine();
        var request = Wallet().BeginExchange(false, _localNow);
        File.SetAttributes(BudgetPath, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Send(engine, request));
            Assert.Equal(0, Store.Load()!.CoinBudgetSeconds);
            Assert.Empty(Store.Load()!.CoinExchanges);
        }
        finally { File.SetAttributes(BudgetPath, FileAttributes.Normal); }
        var retry = Send(engine, request);
        Assert.True(retry.Ok);
        Assert.Equal(60, retry.CoinBudgetSeconds);
    }

    [Fact]
    public void FailedWalletSaveCannotReserveCoinsOrStartAnExchange()
    {
        var wallet = Wallet();
        File.SetAttributes(WalletPath, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => wallet.BeginExchange(false, _localNow));
            Assert.Equal(100, wallet.Balance);
            Assert.Null(wallet.PendingExchange);
        }
        finally { File.SetAttributes(WalletPath, FileAttributes.Normal); }
    }

    [Fact]
    public void FailedReceiptSaveCanBeRecoveredWithoutAnotherTimeCredit()
    {
        var wallet = Wallet();
        var request = wallet.BeginExchange(false, _localNow);
        var response = Send(Engine(), request);
        File.SetAttributes(WalletPath, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => wallet.CompleteExchange(response.ExchangeReceipt!));
            Assert.Equal(request, wallet.PendingExchange);
            Assert.Equal(99, wallet.Balance);
        }
        finally { File.SetAttributes(WalletPath, FileAttributes.Normal); }
        var reopened = Wallet();
        reopened.CompleteExchange(Send(Engine(), request).ExchangeReceipt!);
        Assert.Equal(99, Wallet().Balance);
        Assert.Equal(60, Store.Load()!.CoinBudgetSeconds);
    }

    [Fact]
    public void ReservedCoinsCannotBeBetAndAnotherTradeCannotStartUntilCompletion()
    {
        var wallet = Wallet();
        var request = wallet.BeginExchange(false, _localNow);
        Assert.Throws<ArgumentOutOfRangeException>(() => wallet.PlaceBet(100, 3, 0));
        Assert.Throws<InvalidOperationException>(() => wallet.BeginExchange(true, _localNow));
        var ticket = wallet.PlaceBet(99, 3, 0);
        wallet.CompleteExchange(new CoinExchangeReceipt(request, false, "Reset"));
        Assert.Equal(1, wallet.Balance);
        Assert.Equal(148.50m, wallet.CashOut(ticket.Id, 1.5m));
        Assert.Equal(149.50m, wallet.Balance);
    }

    [Fact]
    public void EmptyWalletsAndFullWalletsCannotStartImpossibleTrades()
    {
        var wallet = Wallet();
        wallet.PlaceBet(100, 3, 0);
        Assert.Throws<InvalidOperationException>(() => wallet.BeginExchange(false, _localNow));
        Assert.Null(wallet.PendingExchange);
        var state = System.Text.Json.JsonSerializer.Deserialize<FlightWalletState>(File.ReadAllText(WalletPath))!;
        File.WriteAllText(WalletPath, System.Text.Json.JsonSerializer.Serialize(state with { Balance = FlightWallet.MaximumBalance, Pending = null }));
        var full = Wallet();
        Assert.Throws<InvalidOperationException>(() => full.BeginExchange(true, _localNow));
        var sale = full.BeginExchange(false, _localNow);
        full.RefreshDailyGrant(_localNow.AddDays(1));
        full.CompleteExchange(new CoinExchangeReceipt(sale, false, "Reset"));
        Assert.Equal(FlightWallet.MaximumBalance, full.Balance);
    }

    [Fact]
    public void MismatchedReceiptsAndOutOfOrderRequestsCannotChangeBalances()
    {
        var wallet = Wallet();
        var request = wallet.BeginExchange(false, _localNow);
        Assert.Throws<InvalidOperationException>(() => wallet.CompleteExchange(new CoinExchangeReceipt(request with { BuyCoins = true }, true)));
        var response = Send(Engine(), request with { Number = 2 });
        Assert.False(response.Ok);
        Assert.Null(response.ExchangeReceipt);
        Assert.Equal(0, response.CoinBudgetSeconds);
        Assert.Equal(99, wallet.Balance);
    }

    private void SeedBudget(double used = 0, bool running = false, int tickAge = 0, bool cooldown = false)
    {
        Store.Save(new TimerState
        {
            LogicalDay = LogicalDay.ForLocal(_localNow), BaseBudgetSeconds = 5400, UsedSeconds = used,
            Running = running, SessionStartUtc = _localNow.AddMinutes(-10).ToUniversalTime(), SessionUsedSeconds = 100,
            LastTickUtc = _localNow.AddSeconds(-tickAge).ToUniversalTime(),
            CooldownUntilUtc = cooldown ? _localNow.AddMinutes(5).ToUniversalTime() : default,
            DotaWeekStart = Limits.WeekStartForLogicalDay(LogicalDay.ForLocal(_localNow)), DotaUsedSeconds = 300,
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
