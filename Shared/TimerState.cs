namespace Shared;

public class TimerState
{
    public int Version { get; set; } = 2;
    // Logical day "yyyy-MM-dd" (03:00 local -> 03:00 local). Budget derives from its weekday.
    public string LogicalDay { get; set; } = "";
    // Total seconds of Steam play accrued today.
    public double UsedSeconds { get; set; }
    // True while a play session is open (Steam seen running, accruing on ticks).
    public bool Running { get; set; }
    public DateTime SessionStartUtc { get; set; }
    // Playing seconds in the current session (resets when Steam closes).
    public double SessionUsedSeconds { get; set; }
    // Manual per-session cap in seconds. 0 = no cap (auto session, limited only by daily budget).
    public int SessionCapSeconds { get; set; }
    public DateTime LastTickUtc { get; set; }
    public bool Warned15m { get; set; }
    public bool SessionWarned { get; set; }
    public bool KillNotified { get; set; }
    // Descendant game exe names seen while Steam was alive (orphan fallback).
    public List<string> SeenGames { get; set; } = new();

    // --- v1 compat (unused by v2 engine, kept so old state.json still deserializes) ---
    public DateTime StartUtc { get; set; }
    public DateTime DeadlineUtc { get; set; }
    public int RequestedSeconds { get; set; }
}

// Pipe messages (one JSON object per connection, newline terminated).
public class PipeRequest
{
    public string Cmd { get; set; } = ""; // "start" | "status"
    public int Seconds { get; set; }
}

public class PipeResponse
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    // Present on status (and on start when ok):
    public bool HasTimer { get; set; }
    public string LogicalDay { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime DeadlineUtc { get; set; }
    public bool Enforcing { get; set; }
    public DateTime ServerNowUtc { get; set; }
    public DateTime NextResetLocal { get; set; }
    // --- v2 budget metering ---
    public int BudgetSeconds { get; set; }
    public double UsedSeconds { get; set; }
    public double RemainingSeconds { get; set; }
    public bool Running { get; set; }
    public bool SteamRunning { get; set; }
    public int SessionCapSeconds { get; set; }
    public double SessionRemainingSeconds { get; set; }
}
