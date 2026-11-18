using App;
using Shared;

public sealed class TimerViewStateTests
{
    private readonly ManualClock _clock = new();
    private TimerViewState View(PipeResponse response)
    {
        var view = new TimerViewState(_clock);
        view.CompleteRequest(view.BeginRequest(), response);
        return view;
    }

    private static PipeResponse Status() => new()
    {
        Ok = true,
        ServerNowUtc = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc),
        SessionMaxSeconds = 7200,
        BudgetSeconds = 14400,
        RemainingSeconds = 14400,
        DotaBudgetSeconds = 28800,
        DotaRemainingSeconds = 28800,
    };

    [Fact]
    public void PausedCountdownIncludesTheNewSessionLimit()
    {
        var view = View(Status());
        Assert.Equal(7200, view.Project(_clock.LocalNow).Playable);
    }

    [Fact]
    public void ActiveDotaLimitsTheHeadlineButOtherGamesKeepTheirDailyAllowance()
    {
        var status = Status();
        status.Running = status.DotaRunning = true;
        status.DotaRemainingSeconds = 90;
        var view = View(status);
        _clock.Advance(10);
        var values = view.Project(_clock.LocalNow);
        Assert.Equal(80, values.Playable);
        Assert.Equal(14390, values.Remaining);

        status.DotaRunning = false;
        Assert.Equal(7190, view.Project(_clock.LocalNow).Playable);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BlockedDotaDoesNotAdvertisePlayableDotaTime(bool enforcing, bool forbidden)
    {
        var status = Status();
        status.Running = status.DotaRunning = true;
        status.DotaEnforcing = enforcing;
        status.DotaForbidden = forbidden;
        var view = View(status);
        _clock.Advance(10);
        Assert.Equal(0, view.Project(_clock.LocalNow).Playable);
        Assert.Equal(28800, view.Project(_clock.LocalNow).DotaRemaining);
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("cooldown")]
    [InlineData("hours")]
    public void GlobalBlocksAlwaysZeroTheHeadlineEvenWhenStopped(string block)
    {
        var status = Status();
        status.Enforcing = block == "daily";
        status.InCooldown = block == "cooldown";
        status.OutsidePlayHours = block == "hours";
        Assert.Equal(0, View(status).Project(_clock.LocalNow).Playable);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("daily")]
    [InlineData("window")]
    public void EstimatesStopAtTheFirstSteamDeadline(string limit)
    {
        var status = Status();
        status.Running = true;
        status.SessionElapsedSeconds = limit == "session" ? 7195 : 0;
        status.RemainingSeconds = limit == "daily" ? 5 : 14400;
        if (limit == "window") status.WindowEndsLocal = _clock.LocalNow.AddSeconds(5);
        var view = View(status);
        _clock.Advance(20);
        var values = view.Project(_clock.LocalNow);
        Assert.Equal(0, values.Playable);
        Assert.Equal(status.RemainingSeconds - 5, values.Remaining);
        Assert.Equal(status.SessionElapsedSeconds + 5, values.SessionElapsed);
    }

    [Fact]
    public void StaleSnapshotsStopEstimatingAndAreNoLongerCurrent()
    {
        var status = Status();
        status.Running = true;
        var view = View(status);
        _clock.Advance(31);
        Assert.False(view.IsCurrent);
        Assert.Equal(14370, view.Project(_clock.LocalNow).Remaining);
        _clock.Advance(120);
        Assert.Equal(14370, view.Project(_clock.LocalNow).Remaining);
    }

    [Fact]
    public void FailureImmediatelyInvalidatesCachedStatusAndAFreshResponseRecoversIt()
    {
        var view = View(Status());
        view.CompleteRequest(view.BeginRequest(), null);
        Assert.False(view.IsCurrent);
        Assert.False(view.ServiceAvailable);
        Assert.NotNull(view.Last);
        view.CompleteRequest(view.BeginRequest(), Status());
        Assert.True(view.IsCurrent);
    }

    [Fact]
    public void OutOfOrderResponsesCannotReplaceNewerResults()
    {
        var view = View(Status());
        long olderRequest = view.BeginRequest();
        long newerRequest = view.BeginRequest();
        _clock.Advance(20);
        Assert.True(view.CompleteRequest(newerRequest, null));
        Assert.False(view.CompleteRequest(olderRequest, Status()));
        Assert.False(view.IsCurrent);
        Assert.Equal(20, view.ElapsedSeconds);

        olderRequest = view.BeginRequest();
        newerRequest = view.BeginRequest();
        Assert.True(view.CompleteRequest(newerRequest, Status()));
        Assert.False(view.CompleteRequest(olderRequest, null));
        Assert.True(view.IsCurrent);
    }

    [Fact]
    public void FreshResponseAfterClockRollbackRestoresStatus()
    {
        var status = Status();
        var view = View(status);
        _clock.Advance(31);
        Assert.False(view.IsCurrent);

        var afterRollback = Status();
        afterRollback.ServerNowUtc = status.ServerNowUtc.AddHours(-1);
        Assert.True(view.CompleteRequest(view.BeginRequest(), afterRollback));
        Assert.Same(afterRollback, view.Last);
        Assert.True(view.IsCurrent);
    }

    [Fact]
    public void CooldownUsesRealElapsedTimeWithoutConsumingPlayBudgets()
    {
        var status = Status();
        status.InCooldown = true;
        status.CooldownRemainingSeconds = 60;
        var view = View(status);
        _clock.Advance(45);
        var values = view.Project(_clock.LocalNow);
        Assert.Equal(15, values.CooldownRemaining);
        Assert.Equal(14400, values.Remaining);
        Assert.Equal(0, values.SessionElapsed);
    }

    [Fact]
    public void ElapsedCooldownStaysBlockedUntilServiceConfirmsExpiry()
    {
        var cooling = Status();
        cooling.InCooldown = true;
        cooling.CooldownRemainingSeconds = 10;
        var view = View(cooling);
        _clock.Advance(11);

        var pending = view.Project(_clock.LocalNow);
        Assert.Equal(0, pending.CooldownRemaining);
        Assert.Equal(0, pending.Playable);

        view.CompleteRequest(view.BeginRequest(), Status());
        Assert.Equal(7200, view.Project(_clock.LocalNow).Playable);
    }

    [Fact]
    public void BorrowLabelsDistinguishCostFromActualGain()
    {
        Assert.Equal("Borrow 10m from tomorrow (+9m 30s today)", Limits.BorrowButtonText(0));
        Assert.Equal("Borrow 10m from next week (+5m this week)", Limits.DotaBorrowButtonText(20));
    }

    [Fact]
    public void WallClockChangesDoNotChangeElapsedTimeOrFreshness()
    {
        var view = View(Status());
        _clock.Advance(10);
        _clock.LocalNow = _clock.LocalNow.AddHours(-1);
        Assert.Equal(10, view.ElapsedSeconds);
        Assert.True(view.IsCurrent);
        _clock.Advance(21);
        Assert.False(view.IsCurrent);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public DateTime LocalNow { get; set; } = new(2026, 11, 14, 12, 0, 0);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(int seconds)
        {
            _timestamp += seconds * TimeSpan.TicksPerSecond;
            LocalNow = LocalNow.AddSeconds(seconds);
        }
    }
}
