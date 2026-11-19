using App;
using Shared;

public sealed class TimerViewStateTests
{
    private readonly ManualClock _clock = new();
    private TimerViewState View(PipeResponse response)
    {
        var view = new TimerViewState(_clock);
        view.Update(response);
        return view;
    }

    private long _sequence;
    private PipeResponse Status() => new()
    {
        Ok = true,
        ServiceInstance = "service-run",
        Sequence = ++_sequence,
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
        view.MarkUnavailable();
        Assert.False(view.IsCurrent);
        Assert.False(view.ServiceAvailable);
        Assert.NotNull(view.Last);
        view.Update(Status());
        Assert.True(view.IsCurrent);
    }

    [Fact]
    public void OlderResponsesCannotRestoreAvailabilityOrResetSnapshotAge()
    {
        var old = Status();
        var view = View(Status());
        _clock.Advance(20);
        view.MarkUnavailable();
        Assert.False(view.Update(old));
        Assert.False(view.IsCurrent);
        Assert.Equal(20, view.ElapsedSeconds);
    }

    [Fact]
    public void ServiceClockGoingBackwardsDoesNotFreezeTheView()
    {
        var view = View(Status());
        _clock.Advance(40);
        var corrected = Status();
        corrected.ServerNowUtc = corrected.ServerNowUtc.AddHours(-1);
        Assert.True(view.Update(corrected));
        Assert.True(view.IsCurrent);
    }

    [Fact]
    public void ARestartedServiceIsAcceptedWithItsNewSequence()
    {
        var view = View(Status());
        var restarted = Status();
        restarted.ServiceInstance = "next-run";
        restarted.Sequence = 1;
        Assert.True(view.Update(restarted));
    }

    [Fact]
    public void UnavailableServiceShowsTheReasonInTheTray()
    {
        var view = View(Status());
        view.MarkUnavailable("The service's reply could not be read.");
        Assert.Equal(("The service's reply could not be read.", TrayLevel.Unknown), TrayStatus.Describe(view, _clock.LocalNow));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(6000, true)] // 20 minutes left in the session, hours left in the day.
    public void TrayUsesThePlayableTimeIncludingTheSessionLimit(double sessionElapsed, bool low)
    {
        var status = Status();
        status.Running = true;
        status.SessionElapsedSeconds = sessionElapsed;
        var (text, level) = TrayStatus.Describe(View(status), _clock.LocalNow);
        Assert.Equal(low ? TrayLevel.Low : TrayLevel.Normal, level);
        Assert.StartsWith("Playing:", text);
    }

    [Fact]
    public void TrayWarnsWhenThePlayWindowIsAboutToClose()
    {
        var status = Status();
        status.WindowEndsLocal = _clock.LocalNow.AddMinutes(10);
        var (text, level) = TrayStatus.Describe(View(status), _clock.LocalNow);
        Assert.Equal(TrayLevel.Low, level);
        Assert.Contains("until", text);
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
