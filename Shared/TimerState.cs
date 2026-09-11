namespace Shared;

public class TimerState
{
    public string LogicalDay { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime DeadlineUtc { get; set; }
    public bool Warned15m { get; set; }
    public bool KillNotified { get; set; }
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
}
