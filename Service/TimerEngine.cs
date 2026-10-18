using Shared;

namespace Service;

// Fully automatic daily budget owner. Single lock guards state; called by Worker tick + pipe threads.
// Model: each logical day (03:00 local) has a base budget (M-F 1h30m, Sat 2h30m,
// Sun 4h) plus up to 30m leftover carried from the previous day, plus optional
// borrows (each -10m tomorrow, +95% then -7pp/-8pp/... of 10m today,
// 0.1%-floor, no Sundays)
// minus yesterday's debt.
// Sessions are automatic: the moment Steam (client or descendant game, + orphan
// fallback) is seen, a session opens and accrues. Each session can use up to 2h
// of the day's budget. When a session ends (Steam closed or 2h max), a cooldown
// blocks Steam equal to that session's length rounded up to 5 minutes.
// Budget exhaustion kills + enforces until 03:00. Everything survives reboot.
// Dota 2 additionally burns a 8h weekly budget (Mon 03:00 reset); when the
// weekly Dota budget is out only Dota is killed, Steam + other games continue.
// Dota is entirely forbidden on Mon / Wed / Fri (logical day): only Dota is killed,
// nothing accrues, Steam + other games continue.
public class TimerEngine
{
    private readonly StateStore _store;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private TimerState? _state;
    private DateTime _lastEnforceKill = DateTime.MinValue;
    private DateTime _lastDotaKill = DateTime.MinValue;
    private readonly ILogger<TimerEngine> _log;

    public event Action<string, string>? Notify; // title, body (UI polls status; service also logs)

    // Max seconds accrued per tick: covers 15s interval + jitter, but a reboot/service
    // gap must not burn the budget while we were down.
    private static readonly TimeSpan MaxTickCredit = TimeSpan.FromSeconds(30);

    public TimerEngine(ILogger<TimerEngine> log, StateStore? store = null, Func<DateTime>? utcNow = null)
    {
        _log = log;
        _store = store ?? new StateStore();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _state = _store.Load();
        if (_state != null && _state.Version != 5)
        {
            _log.LogInformation("Discarding v{Old} state; switching to automatic budget model.", _state.Version);
            _state = null;
            _store.Clear();
        }
        var nowUtc = _utcNow();
        var today = LogicalDay.ForLocal(nowUtc.ToLocalTime());
        if (_state != null && _state.LogicalDay != today)
        {
            // Fresh day on boot: roll forward so yesterday's leftover carries in.
            _state = NewDayState(today, nowUtc, _state);
            _store.Save(_state);
        }
        if (_state != null && _state.LastTickUtc == default)
            _state.LastTickUtc = nowUtc;
    }

    private static int BaseSecondsFor(string logicalDay)
    {
        if (DateTime.TryParse(logicalDay, out var d))
            return (int)Limits.DailyBaseFor(d.DayOfWeek).TotalSeconds;
        return (int)Limits.DailyBaseFor(DayOfWeek.Monday).TotalSeconds;
    }

    private static double EffectiveOf(TimerState st) => Math.Max(0,
        (double)st.BaseBudgetSeconds + st.CarryInSeconds + st.BorrowedSeconds - st.AdvanceDeductionSeconds);

    private static double EffectiveDotaOf(TimerState st) => Math.Max(0,
        Limits.DotaWeeklyBudget.TotalSeconds + st.DotaBorrowedSeconds - st.DotaAdvanceDeductionSeconds);

    // Tomorrow's base budget (for the borrow cap + display).
    private static int TomorrowBaseFor(string logicalDay)
    {
        if (DateTime.TryParse(logicalDay, out var d))
            return (int)Limits.DailyBaseFor(d.AddDays(1).DayOfWeek).TotalSeconds;
        return (int)Limits.DailyBaseFor(DayOfWeek.Monday).TotalSeconds;
    }

    // Leftover (capped at 30m) carried from the immediately previous logical day.
    private static int AdjacentCarry(TimerState? old, string today)
    {
        if (old == null) return 0;
        if (!DateTime.TryParse(old.LogicalDay, out var oldDate)) return 0;
        if (!DateTime.TryParse(today, out var todayDate)) return 0;
        if (oldDate.Date != todayDate.Date.AddDays(-1)) return 0; // multi-day gap: no carry
        double effective = EffectiveOf(old);
        double leftover = Math.Max(0, effective - old.UsedSeconds);
        return (int)Math.Min(Limits.MaxCarryover.TotalSeconds, leftover);
    }

    private static TimerState NewDayState(string today, DateTime nowUtc, TimerState? old)
    {
        string week = Limits.WeekStartForLogicalDay(today);
        // Yesterday's borrows become today's deduction — but only from the
        // immediately previous day (multi-day gaps forgive the debt, same as carry).
        int deduction = 0;
        if (old != null && DateTime.TryParse(old.LogicalDay, out var oldDate)
            && DateTime.TryParse(today, out var todayDate)
            && oldDate.Date == todayDate.Date.AddDays(-1))
            deduction = old.BorrowCount * (int)Limits.BorrowCost.TotalSeconds;
        var st = new TimerState
        {
            Version = 5,
            LogicalDay = today,
            BaseBudgetSeconds = BaseSecondsFor(today),
            CarryInSeconds = AdjacentCarry(old, today),
            AdvanceDeductionSeconds = deduction,
            BorrowedSeconds = 0,
            BorrowCount = 0,
            LastTickUtc = nowUtc,
            DotaWeekStart = week,
        };
        // Weekly Dota usage survives day rollovers inside the same week
        // (including this week's borrows + this week's advance deduction).
        if (old != null && old.DotaWeekStart == week)
        {
            st.DotaUsedSeconds = old.DotaUsedSeconds;
            st.DotaWarned = old.DotaWarned;
            st.DotaNotified = old.DotaNotified;
            st.DotaBorrowedSeconds = old.DotaBorrowedSeconds;
            st.DotaBorrowCount = old.DotaBorrowCount;
            st.DotaAdvanceDeductionSeconds = old.DotaAdvanceDeductionSeconds;
        }
        else if (old != null && !string.IsNullOrEmpty(old.DotaWeekStart))
        {
            // New Dota week: last week's borrows become this week's deduction —
            // but only for adjacent weeks (multi-week gaps forgive the debt).
            if (DateTime.TryParse(old.DotaWeekStart, out var oldWeek)
                && DateTime.TryParse(week, out var newWeek)
                && newWeek.Date == oldWeek.Date.AddDays(7))
                st.DotaAdvanceDeductionSeconds = old.DotaBorrowCount * (int)Limits.BorrowCost.TotalSeconds;
        }
        return st;
    }

    private TimerState EnsureDay(string today, DateTime nowUtc)
    {
        if (_state == null || _state.LogicalDay != today)
        {
            _state = NewDayState(today, nowUtc, _state);
            _store.Save(_state);
            _log.LogInformation("New logical day {Day}, base {Base}s + carry {Carry}s.",
                today, _state.BaseBudgetSeconds, _state.CarryInSeconds);
        }
        // Backfill for states created before a field existed (defensive).
        if (_state.BaseBudgetSeconds <= 0)
        {
            _state.BaseBudgetSeconds = BaseSecondsFor(today);
            _store.Save(_state);
        }
        if (string.IsNullOrEmpty(_state.DotaWeekStart))
        {
            _state.DotaWeekStart = Limits.WeekStartForLogicalDay(today);
            _store.Save(_state);
        }
        return _state;
    }

    // Monday 03:00 weeks: reset weekly Dota usage when the week turns over
    // (normally happens via day rollover, this is the guard).
    // Last week's Dota borrows become this week's deduction (adjacent weeks
    // only; multi-week gaps forgive the debt, same as the daily model).
    private void EnsureWeek(TimerState st, string today)
    {
        string week = Limits.WeekStartForLogicalDay(today);
        if (st.DotaWeekStart != week)
        {
            int deduction = 0;
            if (!string.IsNullOrEmpty(st.DotaWeekStart)
                && DateTime.TryParse(st.DotaWeekStart, out var oldWeek)
                && DateTime.TryParse(week, out var newWeek)
                && newWeek.Date == oldWeek.Date.AddDays(7))
                deduction = st.DotaBorrowCount * (int)Limits.BorrowCost.TotalSeconds;
            st.DotaWeekStart = week;
            st.DotaUsedSeconds = 0;
            st.DotaWarned = false;
            st.DotaNotified = false;
            st.DotaBorrowedSeconds = 0;
            st.DotaBorrowCount = 0;
            st.DotaAdvanceDeductionSeconds = deduction;
            _lastDotaKill = DateTime.MinValue;
            _store.Save(st);
            _log.LogInformation("New Dota week {Week}, 8h budget.", week);
        }
    }

    public PipeResponse Handle(PipeRequest? req)
    {
        lock (_lock)
        {
            var nowUtc = _utcNow();
            var nowLocal = nowUtc.ToLocalTime();
            var today = LogicalDay.ForLocal(nowLocal);
            var resp = new PipeResponse { ServerNowUtc = nowUtc, NextResetLocal = LogicalDay.NextResetLocal(nowLocal), LogicalDay = today };
            var st = EnsureDay(today, nowUtc);
            EnsureWeek(st, today);

            if (req == null || string.IsNullOrWhiteSpace(req.Cmd))
                return Fail("empty request", resp);
            if (req.Cmd == "status")
            {
                Fill(resp, st, nowUtc, liveCheck: true);
                return resp;
            }
            if (req.Cmd == "recheck" || req.Cmd == "probe")
            {
                Fill(resp, st, nowUtc, liveCheck: true);
                resp.ActionNote = BuildRecheckNote(resp);
                return resp;
            }
            if (req.Cmd == "kill" || req.Cmd == "stop" || req.Cmd == "forceclose" || req.Cmd == "force-close")
            {
                return ForceClose(st, nowUtc, resp);
            }
            if (req.Cmd == "start")
            {
                Fill(resp, st, nowUtc, liveCheck: true);
                return Fail("Manual sessions were removed — tracking is automatic now. Just launch Steam.", resp);
            }
            if (req.Cmd == "borrow" || req.Cmd == "advance")
            {
                return Borrow(st, nowUtc, resp);
            }
            if (req.Cmd == "borrowdota" || req.Cmd == "dota-borrow" || req.Cmd == "dotaadvance")
            {
                return BorrowDota(st, nowUtc, resp);
            }
            return Fail("unknown cmd", resp);
        }
    }

    private static PipeResponse Fail(string e, PipeResponse r) { r.Ok = false; r.Error = e; return r; }

    private static string BuildRecheckNote(PipeResponse r)
    {
        var bits = new List<string>();
        if (r.LiveClients.Count > 0) bits.Add("client: " + string.Join(", ", r.LiveClients));
        if (r.LiveGames.Count > 0) bits.Add("games: " + string.Join(", ", r.LiveGames));
        if (r.MatchedOrphans.Count > 0) bits.Add("orphans: " + string.Join(", ", r.MatchedOrphans));
        string seen = bits.Count > 0 ? string.Join("; ", bits) : "nothing running";
        string timer = r.Running ? "session OPEN (accruing)" : r.InCooldown ? "in cooldown (blocked, accrues nothing)" : "no session (stopped)";
        string dota = r.DotaRunning ? ", Dota 2 running" : "";
        if (r.DotaForbidden) dota += " (Dota forbidden Mon / Wed / Fri)";
        return $"Detector sees {seen}. Timer: {timer}{dota}.";
    }

    // Borrow 10m of tomorrow for 95% (1st press), then -7pp, -8pp,
    // -9pp, ... per further press, 0.1% floor, today. Repeatable,
    // blocked on Sundays, capped so tomorrow never drops below zero.
    private PipeResponse Borrow(TimerState st, DateTime nowUtc, PipeResponse resp)
    {
        if (DateTime.TryParse(st.LogicalDay, out var day) && day.DayOfWeek == DayOfWeek.Sunday)
        {
            Fill(resp, st, nowUtc, liveCheck: true);
            return Fail("Borrow is unavailable on Sundays — enjoy the 4h budget.", resp);
        }
        int cost = (int)Limits.BorrowCost.TotalSeconds;
        int nextNum = st.BorrowCount + 1;
        double eff = Limits.EffectivenessForBorrowNumber(nextNum);
        int gain = (int)Limits.BorrowGainForNumber(nextNum).TotalSeconds;
        int tomorrowBase = TomorrowBaseFor(st.LogicalDay);
        int debtAfter = nextNum * cost;
        if (debtAfter > tomorrowBase)
        {
            Fill(resp, st, nowUtc, liveCheck: true);
            int left = Math.Max(0, tomorrowBase - st.BorrowCount * cost);
            return Fail($"Tomorrow only has {Limits.FormatHm(TimeSpan.FromSeconds(tomorrowBase))} base " +
                $"({Limits.FormatHm(TimeSpan.FromSeconds(left))} still borrowable). " +
                $"Another +{Limits.FormatGain(TimeSpan.FromSeconds(gain))} today would push tomorrow below zero.", resp);
        }
        st.BorrowCount++;
        st.BorrowedSeconds += gain;
        _store.Save(st);
        _log.LogInformation("Borrow #{N}: +{Gain}s today ({Eff} effective), -{Cost}s tomorrow ({Day}).", st.BorrowCount, gain, eff, cost, st.LogicalDay);
        Fill(resp, st, nowUtc, liveCheck: true);
        resp.ActionNote = $"Borrowed 10m of tomorrow for " +
            $"+{Limits.FormatGain(TimeSpan.FromSeconds(gain))} today ({Limits.FormatEffectiveness(eff)} effective, use #{st.BorrowCount}). " +
            $"Tomorrow loses 10m. " +
            $"Cooldowns still apply.";
        return resp;
    }

    // Borrow 10m of NEXT WEEK's Dota budget for 95% (1st press), then -7pp,
    // -8pp, -9pp, ... per further press, 50% floor, this week. Repeatable,
    // any day, capped so next week never drops below zero.
    private PipeResponse BorrowDota(TimerState st, DateTime nowUtc, PipeResponse resp)
    {
        int cost = (int)Limits.BorrowCost.TotalSeconds;
        int nextNum = st.DotaBorrowCount + 1;
        double eff = Limits.EffectivenessForDotaBorrowNumber(nextNum);
        int gain = (int)Limits.DotaBorrowGainForNumber(nextNum).TotalSeconds;
        int nextWeekBase = (int)Limits.DotaWeeklyBudget.TotalSeconds;
        int debtAfter = nextNum * cost;
        if (debtAfter > nextWeekBase)
        {
            Fill(resp, st, nowUtc, liveCheck: true);
            int left = Math.Max(0, nextWeekBase - st.DotaBorrowCount * cost);
            return Fail($"Next week only has {Limits.FormatHm(TimeSpan.FromSeconds(nextWeekBase))} Dota base " +
                $"({Limits.FormatHm(TimeSpan.FromSeconds(left))} still borrowable). " +
                $"Another +{Limits.FormatGain(TimeSpan.FromSeconds(gain))} this week would push next week below zero.", resp);
        }
        st.DotaBorrowCount++;
        st.DotaBorrowedSeconds += gain;
        _store.Save(st);
        _log.LogInformation("Dota borrow #{N}: +{Gain}s this week ({Eff} effective), -{Cost}s next week (week {Week}).", st.DotaBorrowCount, gain, eff, cost, st.DotaWeekStart);
        Fill(resp, st, nowUtc, liveCheck: true);
        resp.ActionNote = $"Borrowed 10m of next week's Dota for " +
            $"+{Limits.FormatGain(TimeSpan.FromSeconds(gain))} this week ({Limits.FormatEffectiveness(eff)} effective, use #{st.DotaBorrowCount}). " +
            $"Next week loses 10m Dota.";
        return resp;
    }

    // Manual "force close to stop the timer": kill Steam + games now and, if a
    // session was open, end it immediately (with the normal cooldown for the
    // time played) so the next tick does not keep accruing.
    private PipeResponse ForceClose(TimerState st, DateTime nowUtc, PipeResponse resp)
    {
        var before = Killer.GetDetailedSnapshot(st.SeenGames, st.GameHistory);
        int n = Killer.KillPlaySet(st.SeenGames, st.GameHistory, _log);
        if (st.SeenGames.Count > 50)
            st.SeenGames.RemoveRange(0, st.SeenGames.Count - 50);
        string note;
        if (st.Running)
        {
            double lenSecs = st.SessionUsedSeconds;
            st.Running = false;
            if (lenSecs > 0)
            {
                var cd = Limits.CooldownForSession(TimeSpan.FromSeconds(lenSecs));
                st.CooldownUntilUtc = nowUtc + cd;
                st.LastSessionSeconds = lenSecs;
                st.SessionUsedSeconds = 0;
                st.SessionWarned = false;
                note = n > 0
                    ? $"Closed {n} Steam process(es). Timer stopped — cooldown {Limits.FormatHm(cd)} (this session ran {Limits.FormatHm(TimeSpan.FromSeconds(lenSecs))})."
                    : $"Timer stopped (session ran {Limits.FormatHm(TimeSpan.FromSeconds(lenSecs))}) but no processes were found to close — cooldown {Limits.FormatHm(cd)}.";
                _log.LogInformation("Manual force-close: killed {N}, session {Len}s ended, cooldown {Cd}.", n, (int)lenSecs, cd);
            }
            else
            {
                st.SessionUsedSeconds = 0;
                st.SessionWarned = false;
                note = n > 0
                    ? $"Closed {n} Steam process(es). No session time had accrued, so no cooldown."
                    : "No session time had accrued and nothing was running — timer already stopped.";
                _log.LogInformation("Manual force-close: killed {N}, no session time.", n);
            }
            _store.Save(st);
        }
        else
        {
            _store.Save(st);
            note = n > 0
                ? $"Closed {n} Steam process(es). No session was open — timer already stopped."
                : "Nothing to close — Steam/games not running, timer already stopped.";
            _log.LogInformation("Manual force-close: killed {N}, no session open.", n);
        }
        var after = Killer.GetDetailedSnapshot(st.SeenGames, st.GameHistory);
        if (st.SeenGames.Count > 50)
        {
            st.SeenGames.RemoveRange(0, st.SeenGames.Count - 50);
            _store.Save(st);
        }
        if (after.Playing)
            note += " Warning: something is still alive — hit force-close again or check the list above.";
        _ = before;
        Fill(resp, st, nowUtc, liveCheck: false, steamCache: after.Playing, dotaCache: after.DotaRunning, detail: after);
        resp.KilledProcesses = n;
        resp.ActionNote = note;
        return resp;
    }

    private void Fill(PipeResponse r, TimerState st, DateTime nowUtc, bool liveCheck,
        bool? steamCache = null, bool? dotaCache = null, Killer.DetailedSnapshot? detail = null)
    {
        bool playing;
        bool dotaRunning;
        Killer.DetailedSnapshot? snap = detail;
        if (liveCheck && steamCache == null && dotaCache == null && snap == null)
        {
            int beforeCount = st.SeenGames.Count;
            snap = Killer.GetDetailedSnapshot(st.SeenGames, st.GameHistory);
            if (st.SeenGames.Count != beforeCount)
            {
                if (st.SeenGames.Count > 50)
                    st.SeenGames.RemoveRange(0, st.SeenGames.Count - 50);
                _store.Save(st);
            }
            playing = snap.Playing;
            dotaRunning = snap.DotaRunning;
        }
        else if (snap != null)
        {
            playing = snap.Playing;
            dotaRunning = snap.DotaRunning;
        }
        else
        {
            playing = steamCache ?? st.Running;
            dotaRunning = dotaCache ?? false;
        }
        double effective = EffectiveOf(st);
        double remaining = Math.Max(0, effective - st.UsedSeconds);
        double cdRem = Math.Max(0, (st.CooldownUntilUtc - nowUtc).TotalSeconds);
        bool inCooldown = cdRem > 0;
        double sessionMax = Limits.SessionMax.TotalSeconds;
        double sessElapsed = st.SessionUsedSeconds;
        double sessRem = Math.Max(0, sessionMax - st.SessionUsedSeconds);
        double dotaBudget = EffectiveDotaOf(st);
        double dotaBase = Limits.DotaWeeklyBudget.TotalSeconds;
        double dotaRem = Math.Max(0, dotaBudget - st.DotaUsedSeconds);
        bool dotaForbidden = Limits.IsDotaForbiddenLogicalDay(st.LogicalDay);
        r.Ok = true;
        r.HasTimer = st.UsedSeconds > 0 || st.Running || remaining <= 0 || inCooldown || st.DotaUsedSeconds > 0;
        r.LogicalDay = st.LogicalDay;
        r.StartUtc = st.SessionStartUtc;
        // Compat: deadline = next interesting instant under continuous play.
        // In cooldown nothing accrues, so it is the cooldown end.
        r.DeadlineUtc = inCooldown ? st.CooldownUntilUtc
            : remaining <= 0 ? nowUtc : nowUtc.AddSeconds(Math.Min(remaining, sessRem));
        r.Enforcing = remaining <= 0;
        r.BudgetSeconds = (int)effective;
        r.BaseBudgetSeconds = st.BaseBudgetSeconds;
        r.CarryInSeconds = st.CarryInSeconds;
        r.BorrowedSeconds = st.BorrowedSeconds;
        r.BorrowCount = st.BorrowCount;
        r.AdvanceDeductionSeconds = st.AdvanceDeductionSeconds;
        r.TomorrowBaseSeconds = TomorrowBaseFor(st.LogicalDay);
        r.NextBorrowEffectiveness = Limits.NextBorrowEffectiveness(st.BorrowCount);
        r.NextBorrowGainSeconds = (int)Limits.BorrowGainForNumber(st.BorrowCount + 1).TotalSeconds;
        r.UsedSeconds = st.UsedSeconds;
        r.RemainingSeconds = remaining;
        r.Running = st.Running;
        r.SteamRunning = playing;
        r.SessionElapsedSeconds = sessElapsed;
        r.SessionRemainingSeconds = sessRem;
        r.LastSessionSeconds = st.LastSessionSeconds;
        r.InCooldown = inCooldown;
        r.CooldownRemainingSeconds = cdRem;
        // Total length of this cooldown (for a draining bar in the UI).
        // Derived from the finished session length; falls back to remaining
        // so the bar still renders if the history was lost.
        double cdTotal = Limits.CooldownForSession(TimeSpan.FromSeconds(st.LastSessionSeconds)).TotalSeconds;
        if (cdTotal <= 0) cdTotal = cdRem;
        if (cdRem > cdTotal) cdTotal = cdRem;
        r.CooldownTotalSeconds = cdTotal;
        r.CooldownUntilUtc = st.CooldownUntilUtc;
        r.SessionMaxSeconds = (int)sessionMax;
        r.DotaWeekStart = st.DotaWeekStart;
        r.DotaBudgetSeconds = (int)dotaBudget;
        r.DotaUsedSeconds = st.DotaUsedSeconds;
        r.DotaRemainingSeconds = dotaRem;
        r.DotaRunning = dotaRunning;
        r.DotaEnforcing = dotaRem <= 0 || dotaForbidden;
        r.DotaForbidden = dotaForbidden;
        r.DotaResetsLocal = Limits.NextWeeklyResetLocal(nowUtc.ToLocalTime());
        r.DotaBaseBudgetSeconds = (int)dotaBase;
        r.DotaBorrowedSeconds = st.DotaBorrowedSeconds;
        r.DotaBorrowCount = st.DotaBorrowCount;
        r.DotaAdvanceDeductionSeconds = st.DotaAdvanceDeductionSeconds;
        r.NextDotaBorrowEffectiveness = Limits.NextDotaBorrowEffectiveness(st.DotaBorrowCount);
        r.NextDotaBorrowGainSeconds = (int)Limits.DotaBorrowGainForNumber(st.DotaBorrowCount + 1).TotalSeconds;
        r.LiveClients = snap?.Clients.Take(10).ToList() ?? new List<string>();
        r.LiveGames = snap?.Games.Select(g => $"{g.Name} ({g.Pid})").Take(10).ToList() ?? new List<string>();
        r.MatchedOrphans = snap?.MatchedOrphans.Take(10).ToList() ?? new List<string>();
        r.RememberedGames = st.SeenGames.Take(20).ToList();
    }

    private static void TrimHistory(TimerState st)
    {
        if (st.GameHistory.Count > 50)
            st.GameHistory = st.GameHistory
                .OrderByDescending(g => g.LastSeenUtc)
                .Take(50)
                .ToList();
    }

    private static string UntilLocal(DateTime utc) => utc.ToLocalTime().ToString("HH:mm");

    // Called every 15s by Worker. Returns notifications to surface.
    public List<(string Title, string Body)> Tick(ILogger log)
    {
        var notes = new List<(string, string)>();
        lock (_lock)
        {
            var nowUtc = _utcNow();
            var nowLocal = nowUtc.ToLocalTime();
            var today = LogicalDay.ForLocal(nowLocal);
            double sessionMax = Limits.SessionMax.TotalSeconds;

            if (_state == null || _state.LogicalDay != today)
            {
                _state = NewDayState(today, nowUtc, _state);
                _store.Save(_state);
                _lastEnforceKill = DateTime.MinValue;
                _log.LogInformation("Day reset at 03:00, base {Base}s + carry {Carry}s.",
                    _state.BaseBudgetSeconds, _state.CarryInSeconds);
                return notes;
            }
            var st = _state;
            EnsureWeek(st, today);

            var snap = Killer.GetPlaySnapshot(st.SeenGames, st.GameHistory);
            if (st.SeenGames.Count > 50)
                st.SeenGames.RemoveRange(0, st.SeenGames.Count - 50);
            bool playing = snap.Playing;
            bool dotaRunning = snap.DotaRunning;

            TimeSpan gap = nowUtc - st.LastTickUtc;
            if (st.LastTickUtc == default || gap < TimeSpan.Zero) gap = TimeSpan.Zero;
            if (gap > MaxTickCredit) gap = MaxTickCredit;
            st.LastTickUtc = nowUtc;

            double effective() => EffectiveOf(st);
            double dailyLeft() => Math.Max(0, effective() - st.UsedSeconds);
            double effectiveDota() => EffectiveDotaOf(st);
            double dotaLeft() => Math.Max(0, effectiveDota() - st.DotaUsedSeconds);

            // Cooldown expiry transition: announce once, then allow play.
            if (st.CooldownUntilUtc != default && nowUtc >= st.CooldownUntilUtc)
            {
                st.CooldownUntilUtc = default;
                st.SessionUsedSeconds = 0;
                st.SessionWarned = false;
                _store.Save(st);
                _log.LogInformation("Cooldown over, new session allowed.");
                notes.Add(("Cooldown over",
                    $"You can play again. {Limits.FormatHm(TimeSpan.FromSeconds(dailyLeft()))} left today."));
                foreach (var n in notes) Notify?.Invoke(n.Item1, n.Item2);
                return notes;
            }

            bool inCooldown = st.CooldownUntilUtc > nowUtc;

            // Mon / Wed / Fri: Dota 2 forbidden. Kill only Dota (60s throttle),
            // accrue nothing to the weekly budget, notify once per day.
            // Steam + other games continue normally on the daily budget.
            bool dotaForbidden = Limits.IsDotaForbiddenLogicalDay(today);
            if (dotaForbidden && dotaRunning)
            {
                if ((nowUtc - _lastDotaKill) >= TimeSpan.FromMinutes(1))
                {
                    int n = Killer.KillDota(log);
                    _lastDotaKill = nowUtc;
                    _log.LogInformation("Dota forbidden day, killed {N} Dota process(es).", n);
                }
                if (!st.DotaForbiddenNotified)
                {
                    st.DotaForbiddenNotified = true;
                    _store.Save(st);
                    notes.Add(("Dota 2 forbidden on Mon / Wed / Fri",
                        "No Dota today — back tomorrow. Steam + other games continue on the daily budget."));
                }
                else _store.Save(st);
            }

            // Budget already exhausted: enforce until 03:00.
            if (st.UsedSeconds >= effective())
            {
                bool first = !st.KillNotified;
                if (first || (nowUtc - _lastEnforceKill) >= TimeSpan.FromMinutes(1))
                {
                    if (playing)
                    {
                        int n = Killer.KillPlaySet(st.SeenGames, st.GameHistory, log);
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

            // Cooldown: block play, accrue nothing.
            if (inCooldown)
            {
                double cdRem = Math.Max(0, (st.CooldownUntilUtc - nowUtc).TotalSeconds);
                if (playing)
                {
                    if ((nowUtc - _lastEnforceKill) >= TimeSpan.FromMinutes(1))
                    {
                        int n = Killer.KillPlaySet(st.SeenGames, st.GameHistory, log);
                        _lastEnforceKill = nowUtc;
                        st.Running = false;
                        st.SessionUsedSeconds = 0;
                        st.SessionWarned = false;
                        _store.Save(st);
                        _log.LogInformation("Cooldown active, blocked launch killed {N}.", n);
                        notes.Add(("Cooldown active",
                            n > 0 ? $"Steam blocked for {Limits.FormatHm(TimeSpan.FromSeconds(cdRem))} more (until {UntilLocal(st.CooldownUntilUtc)}). Closed {n} process(es)." : $"Steam blocked for {Limits.FormatHm(TimeSpan.FromSeconds(cdRem))} more (until {UntilLocal(st.CooldownUntilUtc)})."));
                    }
                    else _store.Save(st);
                }
                else
                {
                    if (st.Running)
                    {
                        st.Running = false;
                        st.SessionUsedSeconds = 0;
                        st.SessionWarned = false;
                        _store.Save(st);
                    }
                    else _store.Save(st);
                }
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
                if (st.UsedSeconds > effective())
                    st.UsedSeconds = effective();
                if (dotaRunning && !dotaForbidden)
                {
                    st.DotaUsedSeconds += gap.TotalSeconds;
                    if (st.DotaUsedSeconds > effectiveDota())
                        st.DotaUsedSeconds = effectiveDota();
                }
                // Per-game identity log (informational, for precise tracking later).
                foreach (var g in snap.Games.GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var rec = st.GameHistory.FirstOrDefault(r =>
                        string.Equals(r.Name, g.Key, StringComparison.OrdinalIgnoreCase));
                    if (rec == null)
                    {
                        rec = new GameRecord { Name = g.Key };
                        st.GameHistory.Add(rec);
                    }
                    rec.TotalSeconds += gap.TotalSeconds;
                    rec.LastSeenUtc = nowUtc;
                    string path = g.FirstOrDefault(x => !string.IsNullOrEmpty(x.Path)).Path ?? "";
                    if (!string.IsNullOrEmpty(path)) rec.Path = path;
                }
                TrimHistory(st);

                double rem = dailyLeft();
                double sessRem = Math.Max(0, sessionMax - st.SessionUsedSeconds);
                double dRem = dotaLeft();

                // 15m budget warning (once per day).
                if (!st.Warned15m && rem <= Limits.WarnBefore.TotalSeconds && rem > 0)
                {
                    st.Warned15m = true;
                    notes.Add(("Steam closes in 15 minutes",
                        $"Daily budget low. {Limits.FormatHm(TimeSpan.FromSeconds(rem))} left, back after 03:00. Save your game."));
                    _log.LogInformation("15m budget warning sent.");
                }
                // Session warning (once per session): the 2h session max dipping
                // under 15m before the daily budget does.
                if (!st.SessionWarned && sessRem <= Limits.WarnBefore.TotalSeconds && sessRem > 0 && sessRem < rem)
                {
                    st.SessionWarned = true;
                    notes.Add(("Session ends soon",
                        $"This session ends in {Limits.FormatHm(TimeSpan.FromSeconds(sessRem))}, then a cooldown. Close Steam now to keep the break short."));
                }
                // Dota weekly warning (once per week, while Dota is running).
                // Skipped on forbidden days (forbidden, not low).
                if (!st.DotaWarned && !dotaForbidden && dotaRunning && dRem <= Limits.WarnBefore.TotalSeconds && dRem > 0)
                {
                    st.DotaWarned = true;
                    notes.Add(("Dota 2 closes in 15 minutes",
                        $"Weekly Dota budget low. {Limits.FormatHm(TimeSpan.FromSeconds(dRem))} left (resets Monday 03:00). Other games are unaffected."));
                    _log.LogInformation("15m Dota warning sent.");
                }
                _store.Save(st);

                // Budget hit exactly on this tick: kill now instead of next tick.
                if (st.UsedSeconds >= effective())
                {
                    int n = Killer.KillPlaySet(st.SeenGames, st.GameHistory, log);
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
                // Dota weekly budget hit: kill ONLY Dota, Steam continues.
                // Skipped on forbidden days (the forbidden block above owns kills/notifications).
                if (!dotaForbidden && st.DotaUsedSeconds >= effectiveDota())
                {
                    bool first = !st.DotaNotified;
                    if (dotaRunning && (nowUtc - _lastDotaKill) >= TimeSpan.FromMinutes(1))
                    {
                        int n = Killer.KillDota(log);
                        _lastDotaKill = nowUtc;
                        _log.LogInformation("Dota budget hit, killed {N} Dota process(es).", n);
                        if (first)
                        {
                            st.DotaNotified = true;
                            _store.Save(st);
                            notes.Add(("Dota 2 time is up",
                                n > 0 ? $"Weekly Dota budget used. Closed {n} Dota process(es). Steam + other games continue on the daily budget." : "Weekly Dota budget used. Dota is blocked until Monday 03:00; other games continue."));
                        }
                        else _store.Save(st);
                    }
                    else if (first && !dotaRunning)
                    {
                        st.DotaNotified = true;
                        _store.Save(st);
                        notes.Add(("Dota 2 time is up",
                            "Weekly Dota budget used. Dota is blocked until Monday 03:00; other games continue on the daily budget."));
                    }
                    else _store.Save(st);
                }
                // Session max reached (budget still remains): end session, cooldown.
                if (st.SessionUsedSeconds >= sessionMax)
                {
                    int n = Killer.KillPlaySet(st.SeenGames, st.GameHistory, log);
                    _lastEnforceKill = nowUtc;
                    var len = TimeSpan.FromSeconds(st.SessionUsedSeconds);
                    var cd = Limits.CooldownForSession(len);
                    st.CooldownUntilUtc = nowUtc + cd;
                    _log.LogInformation("Session max reached, killed {N}. Cooldown until {Until}. Budget left: {Left}s.", n, st.CooldownUntilUtc, rem);
                    st.LastSessionSeconds = st.SessionUsedSeconds;
                    st.SessionUsedSeconds = 0;
                    st.SessionWarned = false;
                    st.Running = false;
                    _store.Save(st);
                    notes.Add(("Session limit reached",
                        n > 0 ? $"Closed {n} Steam process(es). {Limits.FormatHm(TimeSpan.FromSeconds(rem))} of today's budget left — next session after {UntilLocal(st.CooldownUntilUtc)} ({Limits.FormatHm(cd)} cooldown)." : $"Session done ({Limits.FormatHm(Limits.SessionMax)} max). {Limits.FormatHm(TimeSpan.FromSeconds(rem))} left — next session after {UntilLocal(st.CooldownUntilUtc)}."));
                    foreach (var n2 in notes) Notify?.Invoke(n2.Item1, n2.Item2);
                    return notes;
                }
            }
            else
            {
                // Steam closed => session ends, cooldown starts (when there was play).
                if (st.Running)
                {
                    bool hadPlay = st.SessionUsedSeconds > 0;
                    st.Running = false;
                    if (hadPlay)
                    {
                        var len = TimeSpan.FromSeconds(st.SessionUsedSeconds);
                        var cd = Limits.CooldownForSession(len);
                        st.CooldownUntilUtc = nowUtc + cd;
                        st.LastSessionSeconds = st.SessionUsedSeconds;
                        st.SessionUsedSeconds = 0;
                        st.SessionWarned = false;
                        _store.Save(st);
                        _log.LogInformation("Session ended by close ({Len}s), cooldown until {Until}. Used {Used}s today.", (int)len.TotalSeconds, st.CooldownUntilUtc, (int)st.UsedSeconds);
                        double rem = dailyLeft();
                        notes.Add(("Session ended — cooldown started",
                            $"{Limits.FormatHm(TimeSpan.FromSeconds(rem))} of today's budget left. Next session after {UntilLocal(st.CooldownUntilUtc)} ({Limits.FormatHm(cd)} cooldown)."));
                    }
                    else
                    {
                        _store.Save(st);
                        _log.LogInformation("Steam closed, session paused. Used {Used}s today.", (int)st.UsedSeconds);
                    }
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
            double effective = EffectiveOf(_state);
            double rem = Math.Max(0, effective - _state.UsedSeconds);
            bool inCd = _state.CooldownUntilUtc > _utcNow();
            DateTime deadline = inCd ? _state.CooldownUntilUtc : _utcNow().AddSeconds(rem);
            return (true, deadline, rem <= 0);
        }
    }
}
