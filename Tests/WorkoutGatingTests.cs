using Microsoft.Extensions.Logging.Abstractions;
using Service;
using Shared;

public sealed class WorkoutGatingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "workout-gating-tests-" + Guid.NewGuid());
    private readonly StateStore _store;
    private readonly FakeProcesses _processes = new();
    private DateTime _now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Local).ToUniversalTime(); // Saturday
    private readonly FakeWorkout _workout = new();
    private TimerEngine Engine() => new(NullLogger<TimerEngine>.Instance, _store, () => _now, _processes, _workout);
    private static PipeResponse Status(TimerEngine engine) => engine.Handle(new() { Cmd = "status" });
    private void Tick(TimerEngine engine, int seconds = 15)
    {
        _now = _now.AddSeconds(seconds);
        engine.Tick(NullLogger.Instance);
    }
    // Tuesday 21:00 local: weekday inside the evening play window, Dota allowed.
    private void SetTuesdayEvening() =>
        _now = new DateTime(2026, 11, 17, 21, 0, 0, DateTimeKind.Local).ToUniversalTime();
    public WorkoutGatingTests() { _store = new StateStore(Path.Combine(_dir, "state.json")); }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public async Task NoEarnedBlocksDotaButNotSteam()
    {
        SetTuesdayEvening(); // weekday: gated
        var engine = Engine();
        _processes.Playing = _processes.Dota = true;
        Tick(engine);
        var status = Status(engine);
        Assert.True(status.WorkoutGatesToday);
        Assert.True(status.DotaEnforcing);
        Assert.Equal(0, status.WorkoutDotaEarnedSeconds);
        Assert.True(status.Running); // daily Steam session continues
        Assert.Equal(1, _processes.DotaKills);
        Assert.Equal(0, _processes.PlayKills);
        // Refresh with no source earnings keeps the block.
        Assert.True(await engine.RefreshWorkoutCreditsAsync());
        Assert.True(Status(engine).DotaEnforcing);
    }

    [Fact]
    public void EarnedUnlocksDotaAndCapsAtWeeklyBase()
    {
        SetTuesdayEvening(); // weekday: gated
        var engine = Engine();
        var week = Limits.WeekStartForLogicalDay(LogicalDay.ForLocal(_now.ToLocalTime()));
        engine.ApplyWorkoutEarned(week, 3600, _now);
        var status = Status(engine);
        Assert.Equal(3600, status.WorkoutDotaEarnedSeconds);
        Assert.Equal(3600, status.DotaBudgetSeconds);
        Assert.Equal(3600, status.DotaRemainingSeconds);
        Assert.False(status.DotaEnforcing);
        engine.ApplyWorkoutEarned(week, 100_000, _now);
        Assert.Equal((int)Limits.DotaWeeklyBudget.TotalSeconds, Status(engine).DotaBudgetSeconds);
    }

    [Fact]
    public void BorrowAddsOnTopOfEarned()
    {
        SetTuesdayEvening(); // weekday: gated
        var engine = Engine();
        var week = Limits.WeekStartForLogicalDay(LogicalDay.ForLocal(_now.ToLocalTime()));
        engine.ApplyWorkoutEarned(week, 3600, _now);
        var borrowed = engine.Handle(new() { Cmd = "borrowdota" });
        Assert.True(borrowed.Ok);
        Assert.Equal(3600, borrowed.WorkoutDotaEarnedSeconds);
        Assert.Equal(570, borrowed.DotaBorrowedSeconds); // 95% of 10m first press
        Assert.Equal(4170, borrowed.DotaBudgetSeconds);
    }

    [Fact]
    public void WeeklyRolloverClearsEarned()
    {
        var engine = Engine();
        var week = Limits.WeekStartForLogicalDay(LogicalDay.ForLocal(_now.ToLocalTime()));
        engine.ApplyWorkoutEarned(week, 3600, _now);
        Assert.Equal(3600, Status(engine).WorkoutDotaEarnedSeconds);
        _now = _now.AddDays(2); // Monday: new Dota week (forbidden day, so no Dota processes)
        Tick(engine);
        var next = Status(engine);
        Assert.Equal(0, next.WorkoutDotaEarnedSeconds);
        Assert.Equal(0, next.DotaUsedSeconds);
        Assert.Equal(week == next.DotaWeekStart ? 3600 : 0, next.WorkoutDotaEarnedSeconds);
    }

    [Fact]
    public async Task FailedSyncKeepsCachedEarned()
    {
        var engine = Engine();
        var week = Limits.WeekStartForLogicalDay(LogicalDay.ForLocal(_now.ToLocalTime()));
        engine.ApplyWorkoutEarned(week, 1800, _now);
        _workout.Throw = true;
        Assert.False(await engine.RefreshWorkoutCreditsAsync());
        Assert.Equal(1800, Status(engine).WorkoutDotaEarnedSeconds);
        _workout.Throw = false;
        _workout.Earned = 2400;
        Assert.True(await engine.RefreshWorkoutCreditsAsync());
        Assert.Equal(2400, Status(engine).WorkoutDotaEarnedSeconds);
    }

    [Fact]
    public void WeekendFollowsOldFreeRules()
    {
        var engine = Engine(); // Saturday: free grant, no workout needed
        Assert.Equal((int)Limits.DotaWeeklyBudget.TotalSeconds, Status(engine).DotaBudgetSeconds);
        Assert.False(Status(engine).WorkoutGatesToday);
        _processes.Playing = _processes.Dota = true;
        Tick(engine);
        var status = Status(engine);
        Assert.False(status.DotaEnforcing);
        Assert.Equal(0, _processes.DotaKills);
        Assert.Equal(0, _processes.PlayKills);
        Tick(engine);
        Assert.Equal(15, Status(engine).DotaUsedSeconds);
    }

    [Fact]
    public void StaleWeekEarnedIsIgnored()
    {
        var engine = Engine();
        engine.ApplyWorkoutEarned("2026-11-02", 5000, _now); // old week
        Assert.Equal(0, Status(engine).WorkoutDotaEarnedSeconds);
    }

    [Fact]
    public async Task TimeoutKeepsCreditAndShutdownCancellationPropagates()
    {
        SetTuesdayEvening();
        var engine = Engine();
        var week = Status(engine).DotaWeekStart;
        engine.ApplyWorkoutEarned(week, 1800, _now);
        _workout.Timeout = true;
        Assert.False(await engine.RefreshWorkoutCreditsAsync());
        Assert.Equal(1800, Status(engine).WorkoutDotaEarnedSeconds);
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RefreshWorkoutCreditsAsync(shutdown.Token));
    }

    [Fact]
    public async Task UnconfiguredSourceDoesNotEraseCachedCreditOrClaimSuccessfulSync()
    {
        var engine = new TimerEngine(NullLogger<TimerEngine>.Instance, _store, () => _now, _processes,
            new NullWorkoutCreditSource());
        engine.ApplyWorkoutEarned(Status(engine).DotaWeekStart, 1800, _now);
        var lastSync = Status(engine).WorkoutLastSyncUtc;
        _now = _now.AddMinutes(1);
        Assert.False(await engine.RefreshWorkoutCreditsAsync());
        Assert.Equal(1800, Status(engine).WorkoutDotaEarnedSeconds);
        Assert.Equal(lastSync, Status(engine).WorkoutLastSyncUtc);
    }

    [Fact]
    public void ReducedCreditNeverRefundsAlreadyPlayedDotaTime()
    {
        SetTuesdayEvening();
        var engine = Engine();
        var week = Status(engine).DotaWeekStart;
        engine.ApplyWorkoutEarned(week, 120, _now);
        _processes.Playing = _processes.Dota = true;
        Tick(engine); // Open session.
        for (int i = 0; i < 4; i++) Tick(engine);
        Assert.Equal(60, Status(engine).DotaUsedSeconds);
        engine.ApplyWorkoutEarned(week, 30, _now); // Corrected workout below usage.
        Tick(engine);
        Assert.Equal(60, Status(engine).DotaUsedSeconds);
        Assert.True(Status(engine).DotaEnforcing);
        Assert.Equal(1, _processes.DotaKills);
        engine.ApplyWorkoutEarned(week, 120, _now);
        Assert.Equal(60, Status(engine).DotaRemainingSeconds);
    }

    [Fact]
    public async Task PendingNetworkSyncDoesNotBlockEnforcementAndCancelsOnShutdown()
    {
        SetTuesdayEvening();
        _workout.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = Engine();
        using var worker = new WorkoutSyncWorker(engine, NullLogger<WorkoutSyncWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await _workout.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _processes.Playing = _processes.Dota = true;
            Tick(engine);
            Assert.Equal(1, _processes.DotaKills);
            Assert.True(Status(engine).Running);
            Tick(engine);
            Assert.Equal(15, Status(engine).UsedSeconds);
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(deadline.Token);
            Assert.True(worker.ExecuteTask!.IsCompleted);
        }
    }

    private sealed class FakeWorkout : IWorkoutCreditSource
    {
        public int Earned;
        public bool Throw;
        public bool Timeout;
        public TaskCompletionSource<int>? Pending;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> GetWeeklyEarnedSecondsAsync(string weekStart, string today, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Started.TrySetResult();
            if (Pending is not null) return Pending.Task.WaitAsync(ct);
            if (Timeout) throw new TaskCanceledException("HTTP request timed out");
            if (Throw) throw new HttpRequestException("upstream down");
            return Task.FromResult(Earned);
        }
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
