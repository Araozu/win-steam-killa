using System.Text.Json.Serialization;

namespace Shared;

public class TimerState
{
    public const int CurrentVersion = 8;
    public int Version { get; set; } = CurrentVersion;
    // Logical day "yyyy-MM-dd" (00:00 -> 00:00 UTC-05). Base budget derives
    // from its weekday; effective budget includes carry-in and borrowing.
    public string LogicalDay { get; set; } = "";
    public int BaseBudgetSeconds { get; set; }
    public int CarryInSeconds { get; set; }
    // Borrow/advance: extra granted today (sum of per-press gains, 95% first,
    // -7pp each further press, 0.1% floor) and the deduction applied today for
    // yesterday's borrows (yesterday's count * BorrowCost = 10m each).
    public int BorrowedSeconds { get; set; }
    public int BorrowCount { get; set; }
    public int AdvanceDeductionSeconds { get; set; }
    // Total seconds of Steam play accrued today against the effective budget.
    public double UsedSeconds { get; set; }
    // True while a play session is open (Steam seen running, accruing on ticks).
    public bool Running { get; set; }
    public DateTime SessionStartUtc { get; set; }
    // Playing seconds in the current session (resets when the session ends).
    // A session ends on Steam close, the 2h session max, or budget exhaustion.
    public double SessionUsedSeconds { get; set; }
    // Length of the last finished session (for cooldown display).
    public double LastSessionSeconds { get; set; }
    public DateTime LastTickUtc { get; set; }
    public bool Warned15m { get; set; }
    public bool SessionWarned { get; set; }
    public bool KillNotified { get; set; }
    // Universal split-session policy: no new play session until this UTC time.
    // Set when ANY session ends (close or 2h max). Persists reboot.
    public DateTime CooldownUntilUtc { get; set; }
    // Descendant game exe names seen while Steam was alive (orphan fallback).
    public List<string> SeenGames { get; set; } = new();
    // Observed Steam child games (identity log for precise per-game tracking).
    public List<GameRecord> GameHistory { get; set; } = new();
    // Both Dota budgets reset Monday 00:00 UTC-05.
    public string DotaWeekStart { get; set; } = "";
    public double DotaWeekdayUsedSeconds { get; set; }
    public double DotaWeekendUsedSeconds { get; set; }
    public long DotaWeekdayEarnedSeconds { get; set; }
    public long DotaWeekendEarnedSeconds { get; set; }
    public bool DotaWarned { get; set; }
    public bool DotaNotified { get; set; }
    [JsonPropertyName("DotaUsedSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double LegacyDotaUsedSeconds { get; set; }
    [JsonPropertyName("WorkoutDotaEarnedSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LegacyDotaEarnedSeconds { get; set; }
    public DateTime WorkoutLastSyncUtc { get; set; }
    // Once-per-day flag: forbidden-day "no Dota" notification already sent.
    // Resets on day rollover (fresh state each logical day).
    public bool DotaForbiddenNotified { get; set; }

    public TimerState Copy()
    {
        var copy = (TimerState)MemberwiseClone();
        copy.SeenGames = new(SeenGames);
        copy.GameHistory = GameHistory.Select(g => new GameRecord
        {
            Name = g.Name, Path = g.Path, TotalSeconds = g.TotalSeconds, LastSeenUtc = g.LastSeenUtc,
        }).ToList();
        return copy;
    }
}

// One observed Steam child game: identity crumbs so we can later map exe names
// to real game titles precisely (Steam gives no AppID to a plain process list).
public class GameRecord
{
    // Bare process name, e.g. "dota2".
    public string Name { get; set; } = "";
    // Full image path when readable, e.g. "...\steamapps\common\dota 2 beta\game\bin\win64\dota2.exe".
    public string Path { get; set; } = "";
    // Total seconds this exe was seen alive across ticks (all-time, informational).
    public double TotalSeconds { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

// Pipe messages (one JSON object per connection, newline terminated).
public class PipeRequest
{
    public string Cmd { get; set; } = "";
    public string? WorkoutBaseUrl { get; set; }
    public string? WorkoutApiToken { get; set; }
}

public sealed record WorkoutSyncStatus(string BaseUrl, bool HasToken, bool Syncing,
    DateTime LastSuccessUtc, long EarnedSeconds, string? LastResult);

// Fields named *Local are policy-clock times (UTC-05), not the PC's time zone.
public class PipeResponse
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public WorkoutSyncStatus? WorkoutSync { get; set; }
    // Response order: Sequence increases within one service run. A new
    // ServiceInstance (service restart) starts a new sequence.
    public string ServiceInstance { get; set; } = "";
    public long Sequence { get; set; }
    // Present on status:
    public string LogicalDay { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public bool Enforcing { get; set; }
    public DateTime ServerNowUtc { get; set; }
    public DateTime NextResetLocal { get; set; }
    // --- v3 daily budget metering (effective = base + carry-in) ---
    public int BudgetSeconds { get; set; }
    public int BaseBudgetSeconds { get; set; }
    public int CarryInSeconds { get; set; }
    // --- borrow/advance (effective = base + carry + borrowed - deduction) ---
    public int BorrowedSeconds { get; set; }
    public int BorrowCount { get; set; }
    public int AdvanceDeductionSeconds { get; set; }
    public int TomorrowBaseSeconds { get; set; }
    // Next press: effectiveness (0.95 first, then -0.07, -0.08, ..., 0.001 floor) + gain seconds.
    public double NextBorrowEffectiveness { get; set; }
    public int NextBorrowGainSeconds { get; set; }
    public double UsedSeconds { get; set; }
    public double RemainingSeconds { get; set; }
    public bool Running { get; set; }
    public bool SteamRunning { get; set; }
    public double SessionElapsedSeconds { get; set; }
    public double SessionRemainingSeconds { get; set; }
    public double LastSessionSeconds { get; set; }
    // --- universal session/cooldown policy ---
    public bool InCooldown { get; set; }
    public double CooldownRemainingSeconds { get; set; }
    public double CooldownTotalSeconds { get; set; }
    public DateTime CooldownUntilUtc { get; set; }
    public int SessionMaxSeconds { get; set; }
    // Active Dota budget and its components.
    public string DotaWeekStart { get; set; } = "";
    public double DotaBudgetSeconds { get; set; }
    public double DotaUsedSeconds { get; set; }
    public double DotaRemainingSeconds { get; set; }
    public bool DotaRunning { get; set; }
    public bool DotaEnforcing { get; set; }
    public DateTime DotaResetsLocal { get; set; }
    public bool DotaWeekendBudget { get; set; }
    public double DotaCarryoverSeconds { get; set; }
    public long WorkoutDotaEarnedSeconds { get; set; }
    public DateTime WorkoutLastSyncUtc { get; set; }
    public bool DotaForbidden { get; set; }
    // --- weekday play windows (Mon–Fri 05:30–06:15 + 20:15–22:00, weekends free) ---
    // True when Steam is currently blocked by the clock (weekday outside windows).
    public bool OutsidePlayHours { get; set; }
    // Next local moment when play becomes allowed (= server local now when allowed).
    public DateTime NextPlayAllowedLocal { get; set; }
    // End of the current allowed window (local). Null when unrestricted/blocked.
    public DateTime? WindowEndsLocal { get; set; }
    // --- live detector breakdown (for the Recheck button / clarity) ---
    // Bare process names seen alive right now, e.g. ["steam", "steamwebhelper"].
    public List<string> LiveClients { get; set; } = new();
    // Descendant game exes alive right now (with pid), e.g. ["cs2 (1234)"].
    public List<string> LiveGames { get; set; } = new();
    // Remembered game exes keeping the timer alive via the orphan fallback
    // (a previously-seen game exe still running after Steam exited).
    public List<string> MatchedOrphans { get; set; } = new();
    // All remembered game exe names (orphan fallback list from state.json).
    public List<string> RememberedGames { get; set; } = new();
    // Processes closed by the last "kill" request.
    public int KilledProcesses { get; set; }
    // Human-readable note for "recheck"/"kill" responses.
    public string ActionNote { get; set; } = "";
}
