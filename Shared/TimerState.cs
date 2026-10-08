namespace Shared;

public class TimerState
{
    public int Version { get; set; } = 3;
    // Logical day "yyyy-MM-dd" (03:00 local -> 03:00 local). Base budget derives
    // from its weekday; effective budget includes carry-in, borrowing, and coins.
    public string LogicalDay { get; set; } = "";
    public int BaseBudgetSeconds { get; set; }
    public int CarryInSeconds { get; set; }
    // Borrow/advance: extra granted today (sum of per-press gains, 95% first,
    // -7pp each further press, 0.1% floor) and the deduction applied today for
    // yesterday's borrows (yesterday's count * BorrowCost = 10m each).
    public int BorrowedSeconds { get; set; }
    public int BorrowCount { get; set; }
    public int AdvanceDeductionSeconds { get; set; }
    // Net budget added by coin sales or spent on coin purchases, today only.
    public int CoinBudgetSeconds { get; set; }
    // Latest durable receipt per wallet; retained across day rollovers.
    public Dictionary<Guid, CoinExchangeReceipt> CoinExchanges { get; set; } = new();
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
    // --- Dota 2 weekly budget (resets Monday 03:00 local) ---
    // Monday "yyyy-MM-dd" of the current logical week.
    public string DotaWeekStart { get; set; } = "";
    public double DotaUsedSeconds { get; set; }
    public bool DotaWarned { get; set; }
    public bool DotaNotified { get; set; }

    // Exchange commits replace the receipt dictionary before saving this snapshot.
    public TimerState Copy() => (TimerState)MemberwiseClone();
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
    public string Cmd { get; set; } = ""; // "status" | "recheck" | "kill" | "borrow" | "coin-exchange"
    public int Seconds { get; set; }
    public CoinExchangeRequest? Exchange { get; set; }
}

public class PipeResponse
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    // Present on status:
    public bool HasTimer { get; set; }
    public string LogicalDay { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime DeadlineUtc { get; set; }
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
    public int CoinBudgetSeconds { get; set; }
    public bool SupportsCoinExchange { get; set; }
    public CoinExchangeReceipt? ExchangeReceipt { get; set; }
    public int TomorrowBaseSeconds { get; set; }
    // Next press: effectiveness (0.95 first, -0.07 each, 0.001 floor) + gain seconds.
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
    // --- Dota 2 weekly budget ---
    public string DotaWeekStart { get; set; } = "";
    public int DotaBudgetSeconds { get; set; }
    public double DotaUsedSeconds { get; set; }
    public double DotaRemainingSeconds { get; set; }
    public bool DotaRunning { get; set; }
    public bool DotaEnforcing { get; set; }
    public DateTime DotaResetsLocal { get; set; }
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
