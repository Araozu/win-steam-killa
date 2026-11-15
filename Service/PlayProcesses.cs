using Shared;

namespace Service;

// Keep Windows process access at the edge so the policy can be tested on any OS.
public interface IPlayProcesses
{
    Killer.DetailedSnapshot Snapshot(ICollection<string> seenGames, IEnumerable<GameRecord> history);
    int KillPlaySet(IEnumerable<string> seenGames, IEnumerable<GameRecord> history, ILogger log);
    int KillDota(ILogger log);
}

public sealed class WindowsPlayProcesses : IPlayProcesses
{
    public Killer.DetailedSnapshot Snapshot(ICollection<string> seenGames, IEnumerable<GameRecord> history) =>
        Killer.GetDetailedSnapshot(seenGames, history);
    public int KillPlaySet(IEnumerable<string> seenGames, IEnumerable<GameRecord> history, ILogger log) =>
        Killer.KillPlaySet(seenGames, history, log);
    public int KillDota(ILogger log) => Killer.KillDota(log);
}
