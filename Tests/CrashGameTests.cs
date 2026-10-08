using App.Game;

namespace Tests;

public sealed class CrashGameTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SteamTimerTests", Guid.NewGuid().ToString("N"));
    private readonly DateTime _today = new(2026, 10, 8, 12, 0, 0);
    private double _clock;
    private string WalletPath => Path.Combine(_directory, "wallet.json");
    private FlightWallet Wallet() => new(WalletPath, _today);
    private CrashGame Game(FlightWallet wallet, decimal crashAt = 3) => new(wallet, () => _clock, () => crashAt);

    [Fact]
    public void DailyGrantIsOncePerLogicalDayAndSurvivesReopening()
    {
        var wallet = Wallet();
        Assert.Equal(100, wallet.Balance);
        Assert.False(wallet.RefreshDailyGrant(_today));
        Assert.Equal(100, Wallet().Balance);
        Assert.False(wallet.RefreshDailyGrant(_today.Date.AddDays(1).AddHours(2)));
        Assert.True(wallet.RefreshDailyGrant(_today.Date.AddDays(1).AddHours(3)));
        Assert.Equal(200, wallet.Balance);
        Assert.False(wallet.RefreshDailyGrant(_today));
        Assert.Equal(200, wallet.Balance);
    }

    [Fact]
    public void OfflineDaysGrantOnlyCurrentDayAndExistingCoinsCarryOver()
    {
        var wallet = Wallet();
        Assert.True(wallet.RefreshDailyGrant(_today.AddDays(10)));
        Assert.Equal(200, wallet.Balance);
        Assert.False(wallet.RefreshDailyGrant(_today.AddDays(1)));
    }

    [Fact]
    public void StakeIsSavedBeforeCountdownAndCannotBeBetTwice()
    {
        var wallet = Wallet();
        var game = Game(wallet);
        game.Start(10, 0, _today);
        Assert.Equal(FlightPhase.Countdown, game.Phase);
        Assert.Equal(90, wallet.Balance);
        Assert.False(game.CashOut());
        Assert.Throws<InvalidOperationException>(() => game.Start(10, 0, _today));
        var reopened = Wallet();
        Assert.Equal(90, reopened.Balance);
        Assert.Equal(0, Assert.Single(reopened.History).Payout);
        Assert.Single(Wallet().History);
    }

    [Fact]
    public void ManualCashoutPaysOnceAndFlightContinuesUntilCrash()
    {
        var wallet = Wallet();
        var game = Game(wallet);
        game.Start(10, 0, _today);
        _clock = CrashGame.CountdownSeconds + CrashGame.SecondsFor(2) + 0.001;
        Assert.True(game.CashOut());
        Assert.Equal(2, game.CashedAt);
        Assert.Equal(20, game.Payout);
        Assert.Equal(110, wallet.Balance);
        Assert.False(game.CashOut());
        Assert.Equal(FlightPhase.Flying, game.Phase);
        _clock = 100;
        game.Update();
        Assert.Equal(FlightPhase.Crashed, game.Phase);
        Assert.Equal(110, wallet.Balance);
        Assert.Equal(3, Assert.Single(wallet.History).CrashAt);
        Assert.Equal(110, Wallet().Balance);
    }

    [Fact]
    public void AClickAtCrashBoundaryLosesEvenWithoutPriorFrameUpdate()
    {
        var wallet = Wallet();
        var game = Game(wallet, 2);
        game.Start(10, 0, _today);
        _clock = CrashGame.CountdownSeconds + CrashGame.SecondsFor(2);
        Assert.False(game.CashOut());
        Assert.Equal(FlightPhase.Crashed, game.Phase);
        Assert.Equal(90, wallet.Balance);
        Assert.Equal(0, game.Payout);
    }

    [Theory]
    [InlineData(1.01)]
    [InlineData(2)]
    [InlineData(100)]
    public void CashoutJustBeforeCrashCannotRoundUpToTheCrashValue(decimal crashAt)
    {
        var wallet = Wallet();
        var game = Game(wallet, crashAt);
        game.Start(1, 0, _today);
        _clock = Math.BitDecrement(CrashGame.CountdownSeconds + CrashGame.SecondsFor(crashAt));
        Assert.True(game.CashOut());
        Assert.True(game.CashedAt < crashAt);
    }

    [Fact]
    public void AutomaticCashoutResolvesBeforeCrashAfterADelayedOrHiddenFrame()
    {
        var wallet = Wallet();
        var game = Game(wallet, 4);
        game.Start(10, 2, _today);
        _clock = 100;
        game.Update();
        Assert.Equal(2, game.CashedAt);
        Assert.Equal(110, wallet.Balance);
        Assert.Equal(FlightPhase.Crashed, game.Phase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AutomaticCashoutAtOrAboveCrashLoses(decimal crashAt)
    {
        var wallet = Wallet();
        var game = Game(wallet, crashAt);
        game.Start(10, 3, _today);
        _clock = 100;
        game.Update();
        Assert.Equal(0, game.CashedAt);
        Assert.Equal(90, wallet.Balance);
    }

    [Fact]
    public void MaximumAltitudeAutomaticallyCollectsRemainingBet()
    {
        var wallet = Wallet();
        var game = Game(wallet, 100);
        game.Start(1, 100, _today);
        _clock = 100;
        game.Update();
        Assert.Equal(100, game.CashedAt);
        Assert.Equal(199, wallet.Balance);
    }

    [Fact]
    public void RestartAfterCashoutPreservesPayoutWithoutPayingTwice()
    {
        var wallet = Wallet();
        var ticket = wallet.PlaceBet(10, 5, 0);
        Assert.Equal(23.45m, wallet.CashOut(ticket.Id, 2.345m));
        Assert.Equal(23.45m, wallet.CashOut(ticket.Id, 2.345m));
        var reopened = Wallet();
        Assert.Equal(113.45m, reopened.Balance);
        Assert.Equal(23.45m, Assert.Single(reopened.History).Payout);
        Assert.Equal(113.45m, Wallet().Balance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(1.001)]
    public void InvalidBetsCannotChangeWallet(decimal bet)
    {
        var wallet = Wallet();
        Assert.Throws<ArgumentOutOfRangeException>(() => wallet.PlaceBet(bet, 3, 0));
        Assert.Equal(100, wallet.Balance);
        Assert.Equal(100, Wallet().Balance);
    }

    [Fact]
    public void InvalidAutoCashoutCannotSpendCoins()
    {
        var wallet = Wallet();
        Assert.Throws<ArgumentOutOfRangeException>(() => wallet.PlaceBet(10, 3, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => wallet.PlaceBet(10, 3, 100.01m));
        Assert.Equal(100, wallet.Balance);
    }

    [Fact]
    public void HistoryIsBoundedAndNewestFlightComesFirst()
    {
        var wallet = Wallet();
        for (int i = 0; i < 12; i++)
        {
            var ticket = wallet.PlaceBet(1, i + 1, 0);
            wallet.Finish(ticket.Id);
        }
        Assert.Equal(8, wallet.History.Count);
        Assert.Equal(12, wallet.History[0].CrashAt);
        Assert.Equal(8, Wallet().History.Count);
    }

    [Fact]
    public void CorruptWalletIsPreservedRatherThanSilentlyReset()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(WalletPath, "invalid json");
        Assert.Throws<System.Text.Json.JsonException>(Wallet);
        Assert.Equal("invalid json", File.ReadAllText(WalletPath));
    }

    [Fact]
    public void FailedSaveCannotStartFlightOrChangeInMemoryBalance()
    {
        var wallet = Wallet();
        File.SetAttributes(WalletPath, FileAttributes.ReadOnly);
        try
        {
            var game = Game(wallet);
            Assert.Throws<UnauthorizedAccessException>(() => game.Start(10, 0, _today));
            Assert.Equal(FlightPhase.Ready, game.Phase);
            Assert.Equal(100, wallet.Balance);
        }
        finally { File.SetAttributes(WalletPath, FileAttributes.Normal); }
        Assert.Equal(100, Wallet().Balance);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.515, 2)]
    [InlineData(0.903, 10)]
    [InlineData(0.99999, 100)]
    public void CrashDistributionHasKnownQuantilesAndCap(double sample, decimal expected)
        => Assert.Equal(expected, CrashGame.CrashForSample(sample));

    [Fact]
    public void ResetDuringFlightAddsCoinsWithoutChangingTheStake()
    {
        var wallet = Wallet();
        var game = Game(wallet);
        game.Start(10, 2, _today);
        wallet.RefreshDailyGrant(_today.AddDays(1));
        _clock = 100;
        game.Update();
        Assert.Equal(210, wallet.Balance);
        Assert.Equal(10, Assert.Single(wallet.History).Bet);
    }

    [Fact]
    public void WalletRejectsPayoutAtCrashOrAnIncorrectTicket()
    {
        var wallet = Wallet();
        var ticket = wallet.PlaceBet(10, 2, 0);
        Assert.Throws<InvalidOperationException>(() => wallet.CashOut(ticket.Id, 2));
        Assert.Throws<InvalidOperationException>(() => wallet.CashOut(ticket.Id, 100));
        Assert.Throws<InvalidOperationException>(() => wallet.CashOut(Guid.NewGuid(), 1.5m));
        Assert.Equal(90, wallet.Balance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
