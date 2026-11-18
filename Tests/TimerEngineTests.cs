using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Service;
using Shared;

public sealed class TimerEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "steamtimer-tests-" + Guid.NewGuid());
    private readonly StateStore _store;
    private readonly FakeProcesses _processes = new();
    private DateTime _now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Local).ToUniversalTime(); // Saturday
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
            LogicalDay = LogicalDay.ForLocal(_now.ToLocalTime()),
            BaseBudgetSeconds = (int)Limits.DailyBaseFor(_now.ToLocalTime().DayOfWeek).TotalSeconds,
            DotaWeekStart = Limits.WeekStartForLogicalDay(LogicalDay.ForLocal(_now.ToLocalTime())),
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
        state.DotaUsedSeconds = Limits.DotaWeeklyBudget.TotalSeconds - 5;
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
        _now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
        _processes.Playing = true;
        var engine = Engine();
        engine.Tick(NullLogger.Instance);
        Assert.Equal(1, _processes.PlayKills);
        Assert.True(Status(engine).OutsidePlayHours);
        Assert.Equal(0, Status(engine).UsedSeconds);
    }

    [Fact]
    public void ForbiddenDotaDayDoesNotSpendWeeklyBudget()
    {
        _now = new DateTime(2026, 11, 16, 20, 30, 0, DateTimeKind.Local).ToUniversalTime();
        Seed(running: true);
        _processes.Playing = _processes.Dota = true;
        var engine = Engine();
        Tick(engine);
        Assert.Equal(1, _processes.DotaKills);
        Assert.Equal(0, _processes.PlayKills);
        Assert.Equal(0, Status(engine).DotaUsedSeconds);
    }

    [Fact]
    public void DayRolloverPreservesGameIdentityAndAppliesBorrowDebt()
    {
        var state = Seed(used: 8000);
        state.BorrowCount = 2;
        state.BorrowedSeconds = 1098;
        state.SeenGames.Add("testgame");
        state.GameHistory.Add(new() { Name = "testgame", Path = @"C:\Games\testgame.exe", TotalSeconds = 30 });
        state.DotaUsedSeconds = 1234;
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
    public void OvernightSessionStillHitsItsLimitAtDailyReset()
    {
        _now = new DateTime(2026, 11, 15, 2, 59, 45, DateTimeKind.Local).ToUniversalTime();
        // The logical day is still Saturday before 03:00.
        var state = Seed(used: 7185, session: 7185, running: true);
        state.BaseBudgetSeconds = 9000;
        _store.Save(state);
        _processes.Playing = true;
        var engine = Engine();

        // Even a status request can observe the rollover before the next tick.
        _now = _now.AddSeconds(15);
        var status = Status(engine);
        Assert.Equal("2026-11-15", status.LogicalDay);
        Assert.Equal(7200, status.SessionElapsedSeconds);
        Assert.Equal(0, status.UsedSeconds);

        engine.Tick(NullLogger.Instance);
        status = Status(engine);
        Assert.Equal(1, _processes.PlayKills);
        Assert.Equal(7200, status.CooldownRemainingSeconds);
        Assert.False(status.Running);
    }

    [Fact]
    public void TickAcrossResetSplitsDailyUsageWithoutResettingSession()
    {
        _now = new DateTime(2026, 11, 15, 2, 59, 50, DateTimeKind.Local).ToUniversalTime();
        var state = Seed(used: 7190, session: 1800, running: true);
        state.BaseBudgetSeconds = 9000;
        _store.Save(state);
        _processes.Playing = true;
        var engine = Engine();

        Tick(engine);
        var status = Status(engine);
        Assert.Equal(1800, status.CarryInSeconds);
        Assert.Equal(1815, status.SessionElapsedSeconds);
        Assert.Equal(5, status.UsedSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CooldownSurvivesDailyResetAndRestart(bool restart)
    {
        _now = new DateTime(2026, 11, 15, 2, 59, 45, DateTimeKind.Local).ToUniversalTime();
        var state = Seed(used: 3600);
        state.CooldownUntilUtc = _now.AddMinutes(40);
        state.LastSessionSeconds = 3600;
        _store.Save(state);
        var engine = Engine();
        _processes.Playing = true;

        _now = _now.AddSeconds(15);
        if (restart) engine = Engine();
        engine.Tick(NullLogger.Instance);
        var status = Status(engine);
        Assert.Equal("2026-11-15", status.LogicalDay);
        Assert.Equal(2385, status.CooldownRemainingSeconds);
        Assert.Equal(3600, status.CooldownTotalSeconds);
        Assert.Equal(1, _processes.PlayKills);
        Assert.False(status.Running);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnwritableStateDoesNotPreventEnforcingLimits(bool dailyLimit)
    {
        Seed(used: dailyLimit ? 8990 : 7190, session: dailyLimit ? 590 : 7190, running: true);
        _processes.Playing = true;
        var engine = Engine();
        Directory.CreateDirectory(Path.Combine(_dir, "state.json.tmp"));

        Tick(engine);
        Assert.Equal(1, _processes.PlayKills);
        var status = Status(engine);
        Assert.Equal(dailyLimit, status.Enforcing);
        Assert.True(status.InCooldown);

        var borrow = engine.Handle(new() { Cmd = "borrow" });
        Assert.False(borrow.Ok);
        Assert.Equal(0, borrow.BorrowCount);
        Assert.Equal(0, borrow.BorrowedSeconds);
    }

    [Fact]
    public void CooldownExpiryDoesNotSkipEnforcement()
    {
        var state = Seed(used: 9000);
        state.CooldownUntilUtc = _now.AddSeconds(15);
        state.LastSessionSeconds = 600;
        _store.Save(state);
        var engine = Engine();
        _processes.Playing = true;

        Tick(engine);
        Assert.Equal(1, _processes.PlayKills);
        Assert.False(Status(engine).InCooldown);
    }

    [Fact]
    public void ProcessAncestryRejectsReusedAndUnknownParentIdentities()
    {
        Dictionary<int, int> parents = new() { [100] = 200, [200] = 300 };
        HashSet<int> roots = [300];
        Dictionary<int, DateTime> started = new()
        {
            [100] = _now,
            [200] = _now.AddSeconds(-1),
            [300] = _now.AddSeconds(-2),
        };
        DateTime? StartTime(int pid) => started.TryGetValue(pid, out var time) ? time : null;

        Assert.True(Killer.HasPlayAncestor(100, roots, parents, StartTime));
        started[200] = _now.AddSeconds(1);
        Assert.False(Killer.HasPlayAncestor(100, roots, parents, StartTime));
        started.Remove(200);
        Assert.False(Killer.HasPlayAncestor(100, roots, parents, StartTime));
    }

    [Fact]
    public void WeeklyRolloverAppliesDotaDebtAndResetsUsage()
    {
        var state = Seed();
        state.DotaBorrowCount = 2;
        state.DotaUsedSeconds = 5000;
        _store.Save(state);
        var engine = Engine();
        _now = _now.AddDays(2);
        Tick(engine);
        Assert.Equal(1200, Status(engine).DotaAdvanceDeductionSeconds);
        Assert.Equal(0, Status(engine).DotaUsedSeconds);
    }

    [Fact]
    public void BorrowCapsProtectTomorrowAndNextWeek()
    {
        var engine = Engine();
        for (int i = 0; i < 24; i++) Assert.True(engine.Handle(new() { Cmd = "borrow" }).Ok);
        Assert.False(engine.Handle(new() { Cmd = "borrow" }).Ok);
        for (int i = 0; i < 48; i++) Assert.True(engine.Handle(new() { Cmd = "borrowdota" }).Ok);
        Assert.False(engine.Handle(new() { Cmd = "borrowdota" }).Ok);
    }

    [Theory]
    [InlineData(2, 59, "2026-11-13")]
    [InlineData(3, 0, "2026-11-14")]
    public void LogicalDayUsesGregorianDateInEveryCulture(int hour, int minute, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            Assert.Equal(expected, LogicalDay.ForLocal(new(2026, 11, 14, hour, minute, 0)));
            Assert.Equal("2026-11-09", Limits.WeekStartForLogicalDay("2026-11-14"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
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
