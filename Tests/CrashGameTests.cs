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
    public void StakeIsSavedDuringBoardingAndCannotBeBetTwice()
    {
        var wallet = Wallet();
        var game = Game(wallet);
        Assert.True(game.Board(10, 0, _today));
        Assert.Equal(FlightPhase.Boarding, game.Phase);
        Assert.Equal(90, wallet.Balance);
        Assert.False(game.CashOut());
        Assert.False(game.Board(10, 0, _today));
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
        Assert.True(game.Board(10, 0, _today));
        _clock = CrashGame.BoardingSeconds + CrashGame.SecondsFor(2) + 0.001;
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
        Assert.True(game.Board(10, 0, _today));
        _clock = CrashGame.BoardingSeconds + CrashGame.SecondsFor(2);
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
        Assert.True(game.Board(1, 0, _today));
        _clock = Math.BitDecrement(CrashGame.BoardingSeconds + CrashGame.SecondsFor(crashAt));
        Assert.True(game.CashOut());
        Assert.True(game.CashedAt < crashAt);
    }

    [Fact]
    public void AutomaticCashoutResolvesBeforeCrashAfterADelayedOrHiddenFrame()
    {
        var wallet = Wallet();
        var game = Game(wallet, 4);
        Assert.True(game.Board(10, 2, _today));
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
        Assert.True(game.Board(10, 3, _today));
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
        Assert.True(game.Board(1, 100, _today));
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
    public void FailedSaveCannotBoardOrChangeInMemoryBalance()
    {
        var wallet = Wallet();
        File.SetAttributes(WalletPath, FileAttributes.ReadOnly);
        try
        {
            var game = Game(wallet);
            Assert.Throws<UnauthorizedAccessException>(() => game.Board(10, 0, _today));
            Assert.Equal(FlightPhase.Boarding, game.Phase);
            Assert.False(game.HasBet);
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
        Assert.True(game.Board(10, 2, _today));
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

    [Fact]
    public void FlightsLaunchAndLoopWithoutABetAndSpectatingCostsNothing()
    {
        var wallet = Wallet();
        var game = Game(wallet);
        Guid firstRound = game.RoundId;
        Assert.Equal(FlightPhase.Boarding, game.Phase);
        Assert.Equal(5d, game.BoardingLeft, 6);
        _clock = 4.99;
        game.Update();
        Assert.Equal(FlightPhase.Boarding, game.Phase);
        _clock = 5 + CrashGame.SecondsFor(1.5m);
        game.Update();
        Assert.Equal(FlightPhase.Flying, game.Phase);
        Assert.False(game.HasBet);
        Assert.False(game.CashOut());
        double crashTime = 5 + CrashGame.SecondsFor(3);
        _clock = crashTime;
        game.Update();
        Assert.Equal(FlightPhase.Crashed, game.Phase);
        Assert.Equal(3, game.Multiplier);
        Assert.Equal(0, Assert.Single(wallet.History).Bet);
        _clock = Math.BitDecrement(crashTime + 5);
        game.Update();
        Assert.Equal(FlightPhase.Crashed, game.Phase);
        Assert.Equal(3, game.Multiplier);
        Assert.Equal(firstRound, game.RoundId);
        Assert.Single(wallet.History);
        _clock = crashTime + 5;
        game.Update();
        Assert.Equal(FlightPhase.Boarding, game.Phase);
        Assert.NotEqual(firstRound, game.RoundId);
        Assert.Equal(5d, game.BoardingLeft, 6);
        double nextLaunch = _clock + 5;
        _clock = Math.BitDecrement(nextLaunch);
        game.Update();
        Assert.Equal(FlightPhase.Boarding, game.Phase);
        _clock = nextLaunch;
        game.Update();
        Assert.Equal(FlightPhase.Flying, game.Phase);
        _clock = nextLaunch + CrashGame.SecondsFor(3) + 0.001;
        game.Update();
        Assert.Equal(2, wallet.History.Count);
        Assert.Equal(100, wallet.Balance);
        Assert.Equal(2, Wallet().History.Count);
    }

    [Fact]
    public void BoardingLateDoesNotDelayLaunchAndCannotJoinAfterTakeoff()
    {
        var wallet = Wallet();
        var game = Game(wallet);
        _clock = 4;
        Assert.True(game.Board(10, 0, _today));
        Assert.Equal(1, game.BoardingLeft);
        _clock = 5;
        game.Update();
        Assert.Equal(FlightPhase.Flying, game.Phase);
        Assert.False(game.Board(10, 0, _today));
        Assert.Equal(90, wallet.Balance);
    }

    [Fact]
    public void BettingAtTheLaunchBoundaryIsRejectedWithoutSpendingCoins()
    {
        var wallet = Wallet();
        var game = Game(wallet);
        _clock = 5;
        Assert.False(game.Board(10, 0, _today));
        Assert.Equal(100, wallet.Balance);
        Assert.False(game.HasBet);
    }

    [Fact]
    public void ABetDoesNotRepeatOnTheFollowingFlight()
    {
        var wallet = Wallet();
        var game = Game(wallet, 2);
        Assert.True(game.Board(10, 0, _today));
        _clock = CrashGame.BoardingSeconds + CrashGame.SecondsFor(2);
        game.Update();
        _clock += CrashGame.ResultSeconds;
        game.Update();
        Assert.False(game.HasBet);
        Assert.Equal(0, game.Bet);
        _clock += CrashGame.BoardingSeconds + CrashGame.SecondsFor(2);
        game.Update();
        Assert.Equal(90, wallet.Balance);
        Assert.Equal(0, wallet.History[0].Bet);
        Assert.Equal(10, wallet.History[1].Bet);
    }

    [Fact]
    public void AnEmptyWalletCanKeepWatchingAutomaticFlights()
    {
        var wallet = Wallet();
        var game = Game(wallet, 1);
        Assert.True(game.Board(100, 0, _today));
        _clock = CrashGame.BoardingSeconds;
        game.Update();
        Assert.Equal(0, wallet.Balance);
        _clock += CrashGame.ResultSeconds;
        game.Update();
        _clock += CrashGame.BoardingSeconds;
        game.Update();
        Assert.Equal(FlightPhase.Crashed, game.Phase);
        Assert.Equal(0, wallet.Balance);
        Assert.Equal(0, wallet.History[0].Bet);
    }

    [Fact]
    public void DelayedClicksCannotBoardOrCollectFromADifferentRound()
    {
        var wallet = Wallet();
        var game = Game(wallet, 2);
        Guid originalRound = game.RoundId;
        _clock = 100;
        game.Update();
        game.Update();
        Assert.False(game.Board(10, 0, _today, originalRound));
        Assert.Equal(100, wallet.Balance);
        Assert.True(game.Board(10, 0, _today, game.RoundId));
        _clock += CrashGame.BoardingSeconds + 0.5;
        Assert.False(game.CashOut(originalRound));
        Assert.True(game.HasLiveBet);
        Assert.Equal(90, wallet.Balance);
    }

    [Fact]
    public void ALongSuspensionSettlesOnceThenOffersAFullBoardingWindow()
    {
        var wallet = Wallet();
        var game = Game(wallet, 4);
        Assert.True(game.Board(10, 2, _today));
        _clock = 86400;
        game.Update();
        Assert.Equal(110, wallet.Balance);
        Assert.Single(wallet.History);
        game.Update();
        Assert.Equal(FlightPhase.Boarding, game.Phase);
        Assert.Equal(5d, game.BoardingLeft, 6);
        Assert.False(game.HasBet);
        Assert.Single(wallet.History);
    }

    [Theory]
    [InlineData(1.01, 1.01, 1.03)]
    [InlineData(1.01, 1.50, 1.52)]
    [InlineData(10, 2, 20)]
    public void PayoutsRoundUpToTheNextCentAndSurviveRestart(decimal bet, decimal multiplier, decimal expected)
    {
        var wallet = Wallet();
        var ticket = wallet.PlaceBet(bet, 5, 0);
        Assert.Equal(expected, wallet.CashOut(ticket.Id, multiplier));
        Assert.Equal(100 - bet + expected, Wallet().Balance);
        Assert.Equal(expected, Assert.Single(Wallet().History).Payout);
    }

    [Fact]
    public void ExistingRoundDownHistoryAndPendingPayoutsStillLoad()
    {
        Directory.CreateDirectory(_directory);
        var legacy = new FlightWalletState
        {
            Balance = 100.01m, LastGrantDay = "2026-10-08",
            Pending = new FlightTicket(Guid.NewGuid(), 1.01m, 5, 0, 1.01m, 1.02m),
            History = [new FlightResult(5, 1.01m, 1.01m, 1.02m)],
        };
        File.WriteAllText(WalletPath, System.Text.Json.JsonSerializer.Serialize(legacy));
        var wallet = Wallet();
        Assert.Equal(100.01m, wallet.Balance);
        Assert.Equal(2, wallet.History.Count);
        Assert.All(wallet.History, result => Assert.Equal(1.02m, result.Payout));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(12)]
    public void PausingFreezesBoardingFlightAndResultTimersUntilResumed(double pauseAt)
    {
        var wallet = Wallet();
        var game = Game(wallet);
        _clock = pauseAt;
        game.Pause();
        var phase = game.Phase;
        var round = game.RoundId;
        var multiplier = game.Multiplier;
        var flightSeconds = game.FlightSeconds;
        var boardingLeft = game.BoardingLeft;
        var crashAge = game.CrashAge;
        int history = wallet.History.Count;
        _clock += 86400;
        game.Pause(); // Repeated focus notifications must not reset the pause.
        game.Update();
        Assert.True(game.IsPaused);
        Assert.Equal(phase, game.Phase);
        Assert.Equal(round, game.RoundId);
        Assert.Equal(multiplier, game.Multiplier);
        Assert.Equal(flightSeconds, game.FlightSeconds);
        Assert.Equal(boardingLeft, game.BoardingLeft);
        Assert.Equal(crashAge, game.CrashAge);
        Assert.Equal(history, wallet.History.Count);
        Assert.False(game.Board(10, 0, _today));
        Assert.False(game.CashOut());
        game.Resume();
        game.Resume();
        game.Update();
        Assert.False(game.IsPaused);
        Assert.Equal(phase, game.Phase);
        Assert.Equal(round, game.RoundId);
        Assert.Equal(multiplier, game.Multiplier);
        Assert.Equal(boardingLeft, game.BoardingLeft, 6);
        Assert.Equal(crashAge, game.CrashAge, 6);
    }

    [Fact]
    public void APausedBetResumesItsAutomaticCashoutWithoutChargingOrPayingTwice()
    {
        var wallet = Wallet();
        var game = Game(wallet, 4);
        Assert.True(game.Board(10, 2, _today));
        _clock = CrashGame.BoardingSeconds + CrashGame.SecondsFor(1.5m);
        game.Pause();
        _clock += 3600;
        game.Update();
        Assert.Equal(90, wallet.Balance);
        Assert.Equal(0, game.CashedAt);
        game.Resume();
        _clock += CrashGame.SecondsFor(2) - CrashGame.SecondsFor(1.5m) + 0.001;
        game.Update();
        Assert.Equal(2, game.CashedAt);
        Assert.Equal(110, wallet.Balance);
        game.Update();
        Assert.Equal(110, wallet.Balance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
