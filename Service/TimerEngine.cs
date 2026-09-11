using Shared;

namespace Service;

// Single-threaded owner of timer state. Called by Worker tick + pipe threads.
public class TimerEngine
{
    private readonly StateStore _store = new();
    private readonly object _lock = new();
    private TimerState? _state;
    private DateTime _lastEnforceKill = DateTime.MinValue;
    private readonly ILogger<TimerEngine> _log;

    public event Action<string, string>? Notify; // title, body (UI polls status; service also logs)

    public TimerEngine(ILogger<TimerEngine> log)
    {
        _log = log;
        _state = _store.Load();
        // Day rollover on boot: stale state from a previous logical day is dropped.
        var today = LogicalDay.ForLocal(DateTime.Now);
        if (_state != null && _state.LogicalDay != today && DateTime.UtcNow >= _state.DeadlineUtc.AddHours(1))
        {
            // Keep enforcing state until reset even across reboot; only drop if well past deadline + new day.
            if (DateTime.Now >= ResetToday())
            { _state = null; _store.Clear(); }
        }
    }

    private static DateTime ResetToday()
    {
        var now = DateTime.Now;
        var r = now.Date.AddHours(Limits.ResetHourLocal);
        return now < r ? r : r.AddDays(1);
    }

    public PipeResponse Handle(PipeRequest? req)
    {
        lock (_lock)
        {
            var nowUtc = DateTime.UtcNow;
            var nowLocal = DateTime.Now;
            var today = LogicalDay.ForLocal(nowLocal);
            var resp = new PipeResponse { ServerNowUtc = nowUtc, NextResetLocal = LogicalDay.NextResetLocal(nowLocal), LogicalDay = today };

            // Lazy reset at 03:00
            if (_state != null && _state.LogicalDay != today && nowLocal >= nowLocal.Date.AddHours(Limits.ResetHourLocal) && _state.LogicalDay != today)
            {
                // Only allow reset once past the 03:00 boundary into a new logical day.
                _state = null;
                _store.Clear();
            }

            if (req == null || string.IsNullOrWhiteSpace(req.Cmd))
                return Fail("empty request", resp);
            if (req.Cmd == "status")
            {
                Fill(resp);
                return resp;
            }
            if (req.Cmd == "start")
            {
                if (_state != null)
                    return Fail($"Timer already started today ({_state.LogicalDay}). Next timer after 03:00.", resp);
                if (req.Seconds < (int)Limits.MinDuration.TotalSeconds || req.Seconds > (int)Limits.MaxDuration.TotalSeconds)
                    return Fail($"Duration must be 1m..3h (non-overridable).", resp);
                var st = new TimerState
                {
                    LogicalDay = today,
                    StartUtc = nowUtc,
                    DeadlineUtc = nowUtc.AddSeconds(req.Seconds),
                    RequestedSeconds = req.Seconds,
                };
                _state = st;
                _store.Save(st);
                _log.LogInformation("Timer started: {Sec}s, deadline {D:u}", req.Seconds, st.DeadlineUtc);
                Fill(resp);
                resp.Ok = true;
                return resp;
            }
            return Fail("unknown cmd", resp);
        }
    }

    private static PipeResponse Fail(string e, PipeResponse r) { r.Ok = false; r.Error = e; return r; }

    private void Fill(PipeResponse r)
    {
        r.Ok = true;
        if (_state == null) { r.HasTimer = false; return; }
        r.HasTimer = true;
        r.LogicalDay = _state.LogicalDay;
        r.StartUtc = _state.StartUtc;
        r.DeadlineUtc = _state.DeadlineUtc;
        r.Enforcing = DateTime.UtcNow >= _state.DeadlineUtc;
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

            if (_state != null && _state.LogicalDay != today && nowLocal.TimeOfDay >= TimeSpan.FromHours(Limits.ResetHourLocal) && today != _state.LogicalDay)
            {
                // New logical day: allow fresh timer.
                _log.LogInformation("Day reset at 03:00, clearing state.");
                _state = null;
                _store.Clear();
                return notes;
            }
            if (_state == null) return notes;

            var toDeadline = _state.DeadlineUtc - nowUtc;

            // 15m warning (once). If timer <= 15m total, warn at half-time instead? No: warn only if duration > 15m.
            if (!_state.Warned15m && toDeadline <= Limits.WarnBefore && toDeadline > TimeSpan.Zero)
            {
                _state.Warned15m = true;
                _store.Save(_state);
                notes.Add(("Steam closes in 15 minutes",
                    $"Deadline at {_state.DeadlineUtc.ToLocalTime():HH:mm}. Save your game."));
                _log.LogInformation("15m warning sent.");
            }

            if (nowUtc >= _state.DeadlineUtc)
            {
                // Enforce until 03:00: kill at most every 60s (or immediately first time).
                bool first = !_state.KillNotified;
                if (first || (nowUtc - _lastEnforceKill) >= TimeSpan.FromMinutes(1))
                {
                    if (Killer.AnySteamRunning())
                    {
                        int n = Killer.KillAll(log);
                        _lastEnforceKill = nowUtc;
                        _log.LogInformation("Enforcing kill, processes touched: {N}", n);
                        if (first)
                        {
                            _state.KillNotified = true;
                            _store.Save(_state);
                            notes.Add(("Steam time is up",
                                n > 0 ? $"Closed {n} Steam process(es). Blocked until 03:00." : "Deadline reached. Blocked until 03:00."));
                        }
                    }
                    else if (first)
                    {
                        _lastEnforceKill = nowUtc;
                        _state.KillNotified = true;
                        _store.Save(_state);
                        notes.Add(("Steam time is up", "Deadline reached. Steam is already closed. Blocked until 03:00."));
                    }
                }
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
            return (true, _state.DeadlineUtc, DateTime.UtcNow >= _state.DeadlineUtc);
        }
    }
}
