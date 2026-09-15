using Shared;

namespace Service;

// Metered daily budget owner. Single lock guards state; called by Worker tick + pipe threads.
// Model: each logical day (03:00 local) has a fixed budget (M-F 1h30m, Sat 2h30m, Sun 3h).
// Every 15s tick: if Steam (client or descendant game, + orphan fallback) is running,
// accrue elapsed time into UsedSeconds. Steam closed => pause immediately.
// Manual "start" arms a per-session cap; budget exhaustion kills + enforces until 03:00.
public class TimerEngine
{
    private readonly StateStore _store = new();
    private readonly object _lock = new();
    private TimerState? _state;
    private DateTime _lastEnforceKill = DateTime.MinValue;
    private readonly ILogger<TimerEngine> _log;

    public event Action<string, string>? Notify; // title, body (UI polls status; service also logs)

    // Max seconds accrued per tick: covers 15s interval + jitter, but a reboot/service
    // gap must not burn the budget while we were down.
    private static readonly TimeSpan MaxTickCredit = TimeSpan.FromSeconds(30);

    public TimerEngine(ILogger<TimerEngine> log)
    {
        _log = log;
        _state = _store.Load();
        if (_state != null && _state.Version != 2)
        {
            _log.LogInformation("Discarding v1 state (single-timer format); switching to daily budget.");
            _state = null;
            _store.Clear();
        }
        var today = LogicalDay.ForLocal(DateTime.Now);
        if (_state != null && _state.LogicalDay != today)
        {
            // Fresh day on boot: yesterday's usage never carries over.
            _state = null;
            _store.Clear();
        }
        if (_state != null && _state.LastTickUtc == default)
            _state.LastTickUtc = DateTime.UtcNow;
    }

    public static TimeSpan BudgetForLogicalDay(string logicalDay)
    {
        if (DateTime.TryParse(logicalDay, out var d))
            return Limits.DailyBudgetFor(d.DayOfWeek);
        return Limits.MaxDuration;
    }

    private TimerState EnsureDay(string today, DateTime nowUtc)
    {
        if (_state == null || _state.LogicalDay != today)
        {
            _state = new TimerState
            {
                Version = 2,
                LogicalDay = today,
                LastTickUtc = nowUtc,
            };
            _store.Save(_state);
            _log.LogInformation("New logical day {Day}, budget {Budget}.",
                today, BudgetForLogicalDay(today));
        }
        return _state;
    }

    public PipeResponse Handle(PipeRequest? req)
    {
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            var nowLocal = DateTime.Now;
            var today = LogicalDay.ForLocal(nowLocal);
            var budget = BudgetForLogicalDay(today);
            var resp = new PipeResponse { ServerNowUtc = nowUtc, NextResetLocal = LogicalDay.NextResetLocal(nowLocal), LogicalDay = today };
            var st = EnsureDay(today, nowUtc);

            if (req == null || string.IsNullOrWhiteSpace(req.Cmd))
                return Fail("empty request", resp);
            if (req.Cmd == "status")
            {
                Fill(resp, st, budget, nowUtc, liveCheck: true);
                return resp;
            }
            if (req.Cmd == "start")
            {
                double remaining = Math.Max(0, budget.TotalSeconds - st.UsedSeconds);
                if (remaining <= 0)
                    return Fail($"Today's budget is used up. Next budget after 03:00.", resp);
                if (req.Seconds < (int)Limits.MinDuration.TotalSeconds)
                    return Fail($"Minimum is {(int)Limits.MinDuration.TotalMinutes}m.", resp);
                if (req.Seconds > (int)Limits.MaxDuration.TotalSeconds)
                    return Fail($"Maximum is {(int)Limits.MaxDuration.TotalHours}h (non-overridable).", resp);
                if (req.Seconds > remaining + 1)
                    return Fail($"Only {Limits.FormatHm(TimeSpan.FromSeconds(remaining))} left today. Enter that or less.", resp);
                // Arm (or re-arm) this session's cap. If Steam is already running the
                // session starts accruing now; otherwise it applies to the next launch.
                st.SessionCapSeconds = req.Seconds;
                st.SessionUsedSeconds = 0;
                st.SessionWarned = false;
                st.SessionStartUtc = nowUtc;
                bool playing = Killer.IsPlaying(st.SeenGames);
                st.Running = playing;
                _store.Save(st);
                _log.LogInformation("Session cap set: {Sec}s (steam {Steam}).", req.Seconds, playing ? "running" : "not running");
                Fill(resp, st, budget, nowUtc, liveCheck: false, steamCache: playing);
                resp.Ok = true;
                return resp;
            }
            return Fail("unknown cmd", resp);
        }
    }

    private static PipeResponse Fail(string e, PipeResponse r) { r.Ok = false; r.Error = e; return r; }

    private void Fill(PipeResponse r, TimerState st, TimeSpan budget, DateTime nowUtc, bool liveCheck, bool? steamCache = null)
    {
        bool playing = steamCache ?? (liveCheck ? CheckPlaying(st) : st.Running);
        double remaining = Math.Max(0, budget.TotalSeconds - st.UsedSeconds);
        double sessRem = st.SessionCapSeconds > 0
            ? Math.Max(0, st.SessionCapSeconds - st.SessionUsedSeconds)
            : remaining;
        r.Ok = true;
        r.HasTimer = st.UsedSeconds > 0 || st.Running || remaining <= 0;
        r.LogicalDay = st.LogicalDay;
        r.StartUtc = st.SessionStartUtc;
        // Compat: deadline = now + what would remain under continuous play.
        r.DeadlineUtc = remaining <= 0 ? nowUtc : nowUtc.AddSeconds(Math.Min(remaining, sessRem));
        r.Enforcing = remaining <= 0;
        r.BudgetSeconds = (int)budget.TotalSeconds;
        r.UsedSeconds = st.UsedSeconds;
        r.RemainingSeconds = remaining;
        r.Running = st.Running;
        r.SteamRunning = playing;
        r.SessionCapSeconds = st.SessionCapSeconds;
        r.SessionRemainingSeconds = sessRem;
    }

    private bool CheckPlaying(TimerState st)
    {
        int before = st.SeenGames.Count;
        bool playing = Killer.IsPlaying(st.SeenGames);
        if (st.SeenGames.Count != before)
        {
            if (st.SeenGames.Count > 50)
                st.SeenGames.RemoveRange(0, st.SeenGames.Count - 50);
            _store.Save(st);
        }
        return playing;
    }

    // Called every 15s by Worker. Returns notifications to surface.
    public List<(string Title, string Body)> Tick(ILogger log)
    {
        var notes = new List<(string, string)>();
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            var nowLocal = DateTime.Now;
            var today = LogicalDay.ForLocal(nowLocal);
            var budget = BudgetForLogicalDay(today);

            if (_state == null || _state.LogicalDay != today)
            {
                _log.LogInformation("Day reset at 03:00, fresh budget {Budget}.", budget);
                _state = new TimerState { Version = 2, LogicalDay = today, LastTickUtc = nowUtc };
                _store.Save(_state);
                _lastEnforceKill = DateTime.MinValue;
                return notes;
            }
            var st = _state;

            bool playing = CheckPlaying(st);
            TimeSpan gap = nowUtc - st.LastTickUtc;
            if (st.LastTickUtc == default || gap < TimeSpan.Zero) gap = TimeSpan.Zero;
            if (gap > MaxTickCredit) gap = MaxTickCredit;
            st.LastTickUtc = nowUtc;

            double budgetLeft() => Math.Max(0, budget.TotalSeconds - st.UsedSeconds);

            // Budget already exhausted: enforce until 03:00.
            if (st.UsedSeconds >= budget.TotalSeconds)
            {
                bool first = !st.KillNotified;
                if (first || (nowUtc - _lastEnforceKill) >= TimeSpan.FromMinutes(1))
                {
                    if (playing)
                    {
                        int n = Killer.KillPlaySet(st.SeenGames, log);
                        _lastEnforceKill = nowUtc;
                        _log.LogInformation("Budget exhausted, enforcing kill: {N}", n);
                        if (first)
                        {
                            st.KillNotified = true;
                            _store.Save(st);
                            notes.Add(("Steam time is up",
                                n > 0 ? $"Daily budget used. Closed {n} Steam process(es). Back after 03:00." : "Daily budget used. Blocked until 03:00."));
                        }
                    }
                    else if (first)
                    {
                        _lastEnforceKill = nowUtc;
                        st.KillNotified = true;
                        _store.Save(st);
                        notes.Add(("Steam time is up", "Daily budget used. Steam is already closed. Back after 03:00."));
                    }
                }
                else _store.Save(st);
                foreach (var n in notes) Notify?.Invoke(n.Item1, n.Item2);
                return notes;
            }

            if (playing)
            {
                if (!st.Running)
                {
                    // Auto session opens the moment Steam appears.
                    st.Running = true;
                    st.SessionStartUtc = nowUtc;
                    st.SessionUsedSeconds = 0;
                    st.SessionWarned = false;
                    _log.LogInformation("Auto session started (steam detected).");
                }
                st.UsedSeconds += gap.TotalSeconds;
                st.SessionUsedSeconds += gap.TotalSeconds;
                if (st.UsedSeconds > budget.TotalSeconds)
                    st.UsedSeconds = budget.TotalSeconds;

                double rem = budgetLeft();
                double sessRem = st.SessionCapSeconds > 0
                    ? Math.Max(0, st.SessionCapSeconds - st.SessionUsedSeconds)
                    : double.PositiveInfinity;

                // 15m budget warning (once per day).
                if (!st.Warned15m && rem <= Limits.WarnBefore.TotalSeconds && rem > 0)
                {
                    st.Warned15m = true;
                    notes.Add(("Steam closes in 15 minutes",
                        $"Daily budget low. {Limits.FormatHm(TimeSpan.FromSeconds(rem))} left, back after 03:00. Save your game."));
                    _log.LogInformation("15m budget warning sent.");
                }
                // Session-cap warning (once per session, only if tighter than budget warn).
                if (st.SessionCapSeconds > 0 && !st.SessionWarned && sessRem <= Limits.WarnBefore.TotalSeconds && sessRem > 0 && sessRem < rem)
                {
                    st.SessionWarned = true;
                    notes.Add(("Session cap in 15 minutes or less",
                        $"This session ends in {Limits.FormatHm(TimeSpan.FromSeconds(sessRem))}. Close Steam to pause the budget."));
                }
                _store.Save(st);

                // Budget hit exactly on this tick: kill now instead of next tick.
                if (st.UsedSeconds >= budget.TotalSeconds)
                {
                    int n = Killer.KillPlaySet(st.SeenGames, log);
                    _lastEnforceKill = nowUtc;
                    st.KillNotified = true;
                    st.Running = false;
                    _store.Save(st);
                    _log.LogInformation("Budget hit mid-session, killed {N}.", n);
                    notes.Add(("Steam time is up",
                        n > 0 ? $"Daily budget used. Closed {n} Steam process(es). Back after 03:00." : "Daily budget used. Blocked until 03:00."));
                    foreach (var n2 in notes) Notify?.Invoke(n2.Item1, n2.Item2);
                    return notes;
                }
                // Session cap reached (budget still remains): end this session only.
                if (st.SessionCapSeconds > 0 && st.SessionUsedSeconds >= st.SessionCapSeconds)
                {
                    int n = Killer.KillPlaySet(st.SeenGames, log);
                    _log.LogInformation("Session cap reached, killed {N}. Budget left: {Left}s.", n, rem);
                    st.SessionCapSeconds = 0;
                    st.SessionUsedSeconds = 0;
                    st.SessionWarned = false;
                    st.Running = false;
                    _store.Save(st);
                    notes.Add(("Session cap reached",
                        n > 0 ? $"Closed {n} Steam process(es). {Limits.FormatHm(TimeSpan.FromSeconds(rem))} of today's budget left — relaunch Steam for a new session." : $"Session done. {Limits.FormatHm(TimeSpan.FromSeconds(rem))} of today's budget left."));
                }
            }
            else
            {
                // Steam closed => pause immediately, session ends (cap cleared).
                if (st.Running)
                {
                    st.Running = false;
                    st.SessionUsedSeconds = 0;
                    st.SessionCapSeconds = 0;
                    st.SessionWarned = false;
                    _store.Save(st);
                    _log.LogInformation("Steam closed, session paused. Used {Used}s today.", (int)st.UsedSeconds);
                }
                else _store.Save(st);
            }
        }
        foreach (var n in notes) Notify?.Invoke(n.Item1, n.Item2);
        return notes;
    }

    public (bool HasTimer, DateTime DeadlineUtc, bool Enforcing) Snapshot()
    {
        lock (_lock)
        {
            if (_state == null) return (false, default, false);
            var budget = BudgetForLogicalDay(_state.LogicalDay);
            double rem = Math.Max(0, budget.TotalSeconds - _state.UsedSeconds);
            return (true, DateTime.UtcNow.AddSeconds(rem), rem <= 0);
        }
    }
}
