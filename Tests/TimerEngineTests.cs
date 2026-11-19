using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Service;
using Shared;

public sealed class TimerEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "steamtimer-tests-" + Guid.NewGuid());
    private readonly StateStore _store;
    private readonly FakeProcesses _processes = new();
    private DateTime _now = PolicyClock.ToUtc(new DateTime(2026, 11, 14, 12, 0, 0)); // Saturday
    private TimerEngine Engine() => new(NullLogger<TimerEngine>.Instance, _store, () => _now, _processes);
    private static PipeResponse Status(TimerEngine engine) => engine.Handle(new() { Cmd = "status" });
    private void Tick(TimerEngine engine, int seconds = 15)
    {
        _now = _now.AddSeconds(seconds);
        engine.Tick(NullLogger.Instance);
    }
    private TimerState Seed(double used = 0, double session = 0, bool running = false)
    {
        var state = new TimerState
        {
            LogicalDay = LogicalDay.ForUtc(_now),
            BaseBudgetSeconds = (int)Limits.DailyBaseFor(PolicyClock.FromUtc(_now).DayOfWeek).TotalSeconds,
            DotaWeekStart = Limits.WeekStartForLogicalDay(LogicalDay.ForUtc(_now)),
            LastTickUtc = _now.AddHours(-2), UsedSeconds = used, SessionUsedSeconds = session, Running = running,
        };
        _store.Save(state);
        return state;
    }
    public TimerEngineTests() { _store = new StateStore(Path.Combine(_dir, "state.json")); }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void RestartDoesNotChargeDowntime()
    {
        Seed(used: 120, session: 120, running: true);
        _processes.Playing = true;
        var engine = Engine();
        engine.Tick(NullLogger.Instance);
        Assert.Equal(120, Status(engine).UsedSeconds);
        Tick(engine);
        Assert.Equal(135, Status(engine).UsedSeconds);
    }

    [Fact]
    public void NewSessionDoesNotChargeIdleInterval()
    {
        var engine = Engine();
        Tick(engine);
        _processes.Playing = true;
        Tick(engine);
        Assert.Equal(0, Status(engine).UsedSeconds);
        Tick(engine);
        Assert.Equal(15, Status(engine).UsedSeconds);
    }

    [Fact]
    public void DailyLimitStartsCooldownAndBorrowDoesNotBypassIt()
    {
        Seed(used: 8990, session: 590, running: true);
        _processes.Playing = true;
        var engine = Engine();
        Tick(engine);
        var exhausted = Status(engine);
        Assert.True(exhausted.Enforcing);
        Assert.False(exhausted.Running);
        Assert.True(exhausted.InCooldown);
        Assert.Equal(600, exhausted.LastSessionSeconds);
        Assert.Equal(600, exhausted.CooldownRemainingSeconds);
        var borrowed = engine.Handle(new() { Cmd = "borrow" });
        Assert.True(borrowed.Ok);
        Assert.False(borrowed.Enforcing);
        Assert.True(borrowed.InCooldown);
        Assert.Equal(570, borrowed.RemainingSeconds);
    }

    [Fact]
    public void SessionLimitClampsTimeBeforeRoundingCooldown()
    {
        Seed(used: 7195, session: 7195, running: true);
        _processes.Playing = true;
        var engine = Engine();
        Tick(engine);
        var status = Status(engine);
        Assert.Equal(7200, status.UsedSeconds);
        Assert.Equal(7200, status.CooldownRemainingSeconds);
        Assert.False(status.Running);
        Assert.Equal(1, _processes.PlayKills);
    }

    [Fact]
    public void NaturalCloseStartsRoundedCooldown()
    {
        Seed(used: 601, session: 601, running: true);
        var engine = Engine();
        Tick(engine);
        Assert.Equal(900, Status(engine).CooldownRemainingSeconds);
        Assert.Equal(601, Status(engine).LastSessionSeconds);
    }

    [Fact]
    public void FractionalRemainderStillTriggersSessionLimit()
    {
        // Live 2026-11-15: real tick jitter accrues 15.0xx s, so a 2h session
        // lands on 7199.999999999997 and `>= 7200` never fires — no kill, no
        // cooldown, counter frozen just below the limit. The engine must treat
        // a sub-millisecond remainder as reached.
        Seed(used: 7199.999999999997, session: 7199.999999999997, running: true);
        _processes.Playing = true;
        var engine = Engine();
        Tick(engine);
        var status = Status(engine);
        Assert.False(status.Running);
        Assert.True(status.InCooldown);
        Assert.Equal(1, _processes.PlayKills);
    }

    [Fact]
    public void FractionalRemainderStillTriggersDailyLimit()
    {
        // Same floating-point stall for the daily budget: Used just below the
        // effective budget must still enforce + kill + stop the session.
        var state = Seed(used: 8999.999999999997, session: 590, running: true);
        _processes.Playing = true;
        var engine = Engine();
        Tick(engine);
        var status = Status(engine);
        Assert.True(status.Enforcing);
        Assert.False(status.Running);
        Assert.Equal(1, _processes.PlayKills);
    }

    [Fact]
    public void DotaExhaustionKillsOnlyDota()
    {
        var state = Seed(running: true);
        state.DotaWeekendUsedSeconds = Limits.DotaWeekendBase.TotalSeconds - 5;
        _store.Save(state);
        _processes.Playing = _processes.Dota = true;
        var engine = Engine();
        Tick(engine);
        Assert.True(Status(engine).DotaEnforcing);
        Assert.True(Status(engine).Running);
        Assert.Equal(1, _processes.DotaKills);
        Assert.Equal(0, _processes.PlayKills);
    }

    [Fact]
    public void FirstTickEnforcesBlockedHoursWithoutWaitingForNextTick()
    {
        _now = PolicyClock.ToUtc(new DateTime(2026, 11, 16, 12, 0, 0));
        _processes.Playing = true;
        var engine = Engine();
        engine.Tick(NullLogger.Instance);
        Assert.Equal(1, _processes.PlayKills);
        Assert.True(Status(engine).OutsidePlayHours);
        Assert.Equal(0, Status(engine).UsedSeconds);
    }

    [Theory]
    [InlineData(17)] // Tuesday
    [InlineData(19)] // Thursday
    public void ForbiddenDotaDayDoesNotSpendWeekdayBudget(int day)
    {
        _now = PolicyClock.ToUtc(new DateTime(2026, 11, day, 20, 30, 0));
        var state = Seed(running: true);
        state.DotaWeekdayEarnedSeconds = 3600;
        _store.Save(state);
        _processes.Playing = _processes.Dota = true;
        var engine = Engine();
        Tick(engine);
        Assert.Equal(1, _processes.DotaKills);
        Assert.Equal(0, _processes.PlayKills);
        Assert.Equal(0, Status(engine).DotaUsedSeconds);
        Assert.Equal(3600, Status(engine).DotaRemainingSeconds);
    }

    [Fact]
    public void DayRolloverPreservesGameIdentityAndAppliesBorrowDebt()
    {
        var state = Seed(used: 8000);
        state.BorrowCount = 2;
        state.BorrowedSeconds = 1098;
        state.SeenGames.Add("testgame");
        state.GameHistory.Add(new() { Name = "testgame", Path = @"C:\Games\testgame.exe", TotalSeconds = 30 });
        state.DotaWeekendUsedSeconds = 1234;
        _store.Save(state);
        var engine = Engine();
        _now = _now.AddDays(1);
        Tick(engine);
        var next = Status(engine);
        Assert.Equal(1200, next.AdvanceDeductionSeconds);
        Assert.Equal(1800, next.CarryInSeconds);
        Assert.Equal(1234, next.DotaUsedSeconds);
        Assert.Contains("testgame", next.RememberedGames);
        Assert.Single(_store.Load()!.GameHistory);
    }

    [Fact]
    public void MondayMidnightExpiresBothDotaBudgets()
    {
        _now = PolicyClock.ToUtc(new DateTime(2026, 11, 15, 23, 59, 59));
        var state = Seed();
        state.DotaWeekdayEarnedSeconds = 10000;
        state.DotaWeekdayUsedSeconds = 5000;
        state.DotaWeekendEarnedSeconds = 600;
        state.DotaWeekendUsedSeconds = 1000;
        _store.Save(state);
        var engine = Engine();
        Assert.Equal(19000, Status(engine).DotaRemainingSeconds);
        _now = _now.AddSeconds(1);
        var monday = Status(engine);
        Assert.False(monday.DotaWeekendBudget);
        Assert.False(monday.DotaForbidden);
        Assert.Equal(0, monday.DotaBudgetSeconds);
        Assert.Equal(0, monday.DotaUsedSeconds);
        Assert.Equal(0, _store.Load()!.DotaWeekendEarnedSeconds);
    }

    [Fact]
    public void SteamCanBorrowFromTomorrowAndDotaCannotBorrow()
    {
        var engine = Engine();
        for (int i = 0; i < 24; i++) Assert.True(engine.Handle(new() { Cmd = "borrow" }).Ok);
        Assert.False(engine.Handle(new() { Cmd = "borrow" }).Ok);
        Assert.False(engine.Handle(new() { Cmd = "borrowdota" }).Ok);
    }

    [Theory]
    [InlineData(13, false)]
    [InlineData(14, true)]
    public void LegacyDotaUsageMigratesToTheActiveBudget(int day, bool weekend)
    {
        _now = PolicyClock.ToUtc(new DateTime(2026, 11, day, 21, 0, 0));
        var state = Seed(used: 500);
        state.Version = 7;
        state.LegacyDotaUsedSeconds = 300;
        state.LegacyDotaEarnedSeconds = 1800;
        _store.Save(state);
        var status = Status(Engine());
        Assert.Equal(500, status.UsedSeconds);
        Assert.Equal(300, status.DotaUsedSeconds);
        Assert.Equal(weekend, status.DotaWeekendBudget);
        Assert.Equal(TimerState.CurrentVersion, _store.Load()!.Version);
        Assert.Equal(300, Status(Engine()).DotaUsedSeconds);
    }

    [Theory]
    [InlineData("syncthing", true)]
    [InlineData("ConHost", true)]
    [InlineData("hardwareupdater", true)]
    [InlineData("dota2", false)]
    [InlineData("steam", false)]
    public void BackgroundProcessesAreExcludedByExactName(string name, bool ignored)
    {
        Assert.Equal(ignored, Killer.IsIgnoredPlayProcess(name));
    }

    [Theory]
    [InlineData(13, 23, 59, "2026-11-13")]
    [InlineData(14, 0, 0, "2026-11-14")]
    [InlineData(14, 2, 59, "2026-11-14")]
    public void LogicalDayStartsAtMidnightAndUsesGregorianDateInEveryCulture(int day, int hour, int minute, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            Assert.Equal(expected, LogicalDay.For(new(2026, 11, day, hour, minute, 0)));
            Assert.Equal("2026-11-09", Limits.WeekStartForLogicalDay("2026-11-14"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void PolicyClockIsUtcMinusFiveWhateverThePcTimeZone()
    {
        // 04:59 UTC Saturday is still Friday 23:59 at UTC-05.
        Assert.Equal("2026-11-13", LogicalDay.ForUtc(new DateTime(2026, 11, 14, 4, 59, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-11-14", LogicalDay.ForUtc(new DateTime(2026, 11, 14, 5, 0, 0, DateTimeKind.Utc)));
        // Saturday 00:30 is weekend: no play window applies.
        Assert.True(Limits.IsPlayAllowed(PolicyClock.FromUtc(new DateTime(2026, 11, 14, 5, 30, 0, DateTimeKind.Utc))));
        Assert.Equal(new DateTime(2026, 11, 16), Limits.NextWeeklyResetLocal(new DateTime(2026, 11, 15, 23, 59, 0)));
    }

    [Fact]
    public void PlayWindowEndWarnsOnceWhenItEndsPlayFirst()
    {
        _now = PolicyClock.ToUtc(new DateTime(2026, 11, 17, 21, 44, 30)); // Tuesday, window ends 22:00
        _processes.Playing = true;
        var engine = Engine();
        var titles = new List<string>();
        // 21:44:45 opens the session; 21:45:00 is 15 minutes before the window closes.
        for (int i = 0; i < 4; i++)
        {
            _now = _now.AddSeconds(15);
            titles.AddRange(engine.Tick(NullLogger.Instance).Select(note => note.Title));
        }
        Assert.Single(titles, title => title == "Play window closes soon");
        Assert.DoesNotContain("Steam closes in 15 minutes", titles);
    }

    [Fact]
    public void ResponsesCarryAnIncreasingSequence()
    {
        var engine = Engine();
        var first = Status(engine);
        var second = Status(engine);
        Assert.Equal(first.ServiceInstance, second.ServiceInstance);
        Assert.True(second.Sequence > first.Sequence);
    }

    [Fact]
    public void BorrowAfterTheLowBudgetWarningWarnsAgain()
    {
        _now = PolicyClock.ToUtc(new DateTime(2026, 11, 14, 12, 0, 0));
        Seed(used: 9000 - 600, session: 60, running: true); // Saturday: 2h30m budget, 10 minutes left.
        _processes.Playing = true;
        var engine = Engine();
        Tick(engine);
        Assert.True(_store.Load()!.Warned15m);
        Assert.True(engine.Handle(new() { Cmd = "borrow" }).Ok);
        Assert.False(_store.Load()!.Warned15m);
    }

    private sealed class FakeProcesses : IPlayProcesses
    {
        public bool Playing, Dota;
        public int PlayKills, DotaKills;
        public Killer.DetailedSnapshot Snapshot(ICollection<string> seenGames, IEnumerable<GameRecord> history) =>
            new(Playing || Dota, Dota, [], Playing ? ["steam (123)"] : [], []);
        public int KillPlaySet(IEnumerable<string> seenGames, IEnumerable<GameRecord> history, ILogger log)
        { PlayKills++; Playing = Dota = false; return 1; }
        public int KillDota(ILogger log) { DotaKills++; Dota = false; return 1; }
    }
}
