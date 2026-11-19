using System.Diagnostics;
using System.Runtime.InteropServices;
using Shared;

namespace Service;

// Kills Steam client processes + any descendant (games launched via Steam).
// No NuGet needed: parent PIDs via Toolhelp32 snapshot (kernel32).
public static class Killer
{
    public static readonly string[] SteamNames =
        ["steam", "steamwebhelper", "gameoverlayui", "steamservice"];

    // Detection excludes steamservice: it can linger as a Windows service even
    // when the client is closed, which would make auto-start never pause.
    public static readonly string[] ClientNames =
        ["steam", "steamwebhelper", "gameoverlayui"];

    // Dota 2 game processes (weekly budget + Dota-only kill).
    public static readonly string[] DotaNames = ["dota2"];

    // One observed Steam child game process (identity crumbs for per-game tracking).
    public readonly record struct GameSight(string Name, int Pid, string Path);

    public static bool IsSteamName(string processName) =>
        SteamNames.Contains(processName, StringComparer.OrdinalIgnoreCase);

    public static bool IsClientName(string processName) =>
        ClientNames.Contains(processName, StringComparer.OrdinalIgnoreCase);

    public static bool IsDotaName(string processName) =>
        DotaNames.Contains(processName, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> IgnoredPlayProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "syncthing", "conhost", "hardwareupdater",
    };

    internal static bool IsIgnoredPlayProcess(string name) => IgnoredPlayProcesses.Contains(name);

    public static int KillAll(ILogger? log = null) => KillPlaySet(null, log);

    // Kill Steam subtree plus any extra exe names (orphaned games whose Steam
    // parent already exited). extraExeNames are bare process names, any case.
    // Orphans require a recorded matching path (prevents a poisoned remembered
    // name like "launcher" from killing an unrelated process).
    public static int KillPlaySet(IEnumerable<string>? extraExeNames, ILogger? log = null) =>
        KillPlaySet(extraExeNames, knownGames: null, log);

    public static int KillPlaySet(IEnumerable<string>? extraExeNames, IEnumerable<GameRecord>? knownGames, ILogger? log = null)
    {
        try
        {
            var all = Process.GetProcesses();
            try
            {
                var parentOf = BuildParentMap();
                var steamRoots = new HashSet<int>(
                    all.Where(p => IsSteamName(SafeName(p)) || IsDotaName(SafeName(p))).Select(SafeId).Where(id => id > 0));

                var extra = new HashSet<string>(
                    extraExeNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                var knownPaths = KnownPathMap(knownGames);

                // Any process whose ancestor chain hits a steam root is in scope,
                // plus any known orphaned game exe still running.
                var targets = new List<Process>();
                foreach (var p in all)
                {
                    int id = SafeId(p);
                    string name = SafeName(p);
                    if (id <= 0 || IsIgnoredPlayProcess(name)) continue;
                    if (steamRoots.Contains(id) || HasSteamAncestor(id, steamRoots, parentOf))
                        targets.Add(p);
                    else if (extra.Count > 0 && extra.Contains(name)
                        && knownPaths.TryGetValue(name, out var paths)
                        && paths.Contains(SafePath(p), StringComparer.OrdinalIgnoreCase))
                        targets.Add(p);
                }
                return KillTargets(targets, parentOf, log);
            }
            finally { foreach (var p in all) try { p.Dispose(); } catch { } }
        }
        catch (Exception ex) { log?.LogWarning(ex, "KillAll failed"); return 0; }
    }

    // Kill ONLY Dota 2. Steam and other games survive.
    // Targets dota2.exe processes plus anything they spawned.
    public static int KillDota(ILogger? log = null)
    {
        try
        {
            var all = Process.GetProcesses();
            try
            {
                var parentOf = BuildParentMap();
                var dotaRoots = new HashSet<int>(
                    all.Where(p => IsDotaName(SafeName(p))).Select(SafeId).Where(id => id > 0));
                if (dotaRoots.Count == 0) return 0;
                var targets = new List<Process>();
                foreach (var p in all)
                {
                    int id = SafeId(p);
                    if (id <= 0 || IsIgnoredPlayProcess(SafeName(p))) continue;
                    if (dotaRoots.Contains(id) || HasSteamAncestor(id, dotaRoots, parentOf))
                        targets.Add(p);
                }
                return KillTargets(targets, parentOf, log);
            }
            finally { foreach (var p in all) try { p.Dispose(); } catch { } }
        }
        catch (Exception ex) { log?.LogWarning(ex, "KillDota failed"); return 0; }
    }

    // Shared graceful-close then kill, leaves (games) first, roots last.
    private static int KillTargets(List<Process> targets, Dictionary<int, int> parentOf, ILogger? log)
    {
        int killed = 0;
        if (targets.Count == 0) return 0;
        targets.Sort((a, b) =>
            Depth(SafeId(b), parentOf).CompareTo(Depth(SafeId(a), parentOf)));

        foreach (var p in targets)
        {
            try
            {
                if (!p.HasExited)
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
                }
            }
            catch { }
        }
        Thread.Sleep(4000);
        foreach (var p in targets)
        {
            try
            {
                p.Refresh();
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: false);
                    killed++;
                }
                else killed++;
            }
            catch (Exception ex) { log?.LogDebug(ex, "kill failed"); }
        }
        return killed;
    }

    public static bool AnySteamRunning()
    {
        try
        {
            var all = Process.GetProcesses();
            try { return all.Any(p => IsSteamName(SafeName(p))); }
            finally { foreach (var p in all) p.Dispose(); }
        }
        catch { return false; }
    }

    // Single-snapshot view: is Steam play happening, is Dota 2 running, and
    // which Steam child games are alive right now (for identity tracking).
    // Updates seenGames in place with descendant exe names observed right now.
    // The orphan fallback (a remembered game exe still alive after Steam
    // exited) is path-checked when GameHistory has a recorded path for that
    // name: a same-named but different-path process does NOT keep the timer
    // alive. That was the "never stops" poison case (e.g. a generic
    // "launcher" remembered once, then any unrelated launcher kept accruing).
    public static (bool Playing, bool DotaRunning, List<GameSight> Games) GetPlaySnapshot(
        ICollection<string> seenGames) =>
        GetPlaySnapshot(seenGames, knownGames: null);

    public static (bool Playing, bool DotaRunning, List<GameSight> Games) GetPlaySnapshot(
        ICollection<string> seenGames, IEnumerable<GameRecord>? knownGames)
    {
        var snap = GetDetailedSnapshot(seenGames, knownGames);
        return (snap.Playing, snap.DotaRunning, snap.Games);
    }

    public sealed record DetailedSnapshot(
        bool Playing,
        bool DotaRunning,
        List<GameSight> Games,
        List<string> Clients,
        List<string> MatchedOrphans);

    public static DetailedSnapshot GetDetailedSnapshot(
        ICollection<string> seenGames,
        IEnumerable<GameRecord>? knownGames = null)
    {
        var games = new List<GameSight>();
        var clients = new List<string>();
        var orphans = new List<string>();
        try
        {
            var all = Process.GetProcesses();
            try
            {
                var parentOf = BuildParentMap();
                var clientRoots = new HashSet<int>(
                    all.Where(p => IsClientName(SafeName(p))).Select(SafeId).Where(id => id > 0));
                foreach (var p in all.Where(p => IsClientName(SafeName(p))).Take(20))
                    clients.Add($"{SafeName(p)} ({SafeId(p)})");
                bool dotaAlive = false;
                foreach (var p in all)
                {
                    string n = SafeName(p);
                    if (!string.IsNullOrEmpty(n) && IsDotaName(n)) dotaAlive = true;
                }
                foreach (var p in all)
                {
                    int id = SafeId(p);
                    if (id <= 0 || clientRoots.Contains(id)) continue;
                    if (HasSteamAncestor(id, clientRoots, parentOf))
                    {
                        string n = SafeName(p);
                        if (!string.IsNullOrEmpty(n) && !IsIgnoredPlayProcess(n) && !IsSteamName(n))
                            games.Add(new GameSight(n, id, SafePath(p)));
                    }
                }
                foreach (var n in games.Select(g => g.Name).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (!seenGames.Contains(n, StringComparer.OrdinalIgnoreCase))
                        seenGames.Add(n);
                var knownPaths = KnownPathMap(knownGames);
                if (seenGames.Count > 0)
                {
                    // Resolve live paths lazily: only for remembered names, so a
                    // machine with hundreds of processes stays fast.
                    var liveByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in all)
                    {
                        string n = SafeName(p);
                        if (string.IsNullOrEmpty(n) || IsIgnoredPlayProcess(n) || !seenGames.Contains(n, StringComparer.OrdinalIgnoreCase))
                            continue;
                        if (!liveByName.TryGetValue(n, out var list))
                            liveByName[n] = list = new List<string>();
                        if (list.Count < 5)
                            list.Add(SafePath(p));
                    }
                    foreach (var name in seenGames)
                    {
                        if (!liveByName.TryGetValue(name, out var livePaths)) continue;
                        if (OrphanPathsMatch(name, livePaths, knownPaths))
                            orphans.Add($"{name} ({livePaths.Count} alive)");
                        if (orphans.Count >= 20) break;
                    }
                }
                bool playing;
                if (clientRoots.Count > 0 || games.Count > 0 || dotaAlive) playing = true;
                else if (orphans.Count > 0) playing = true;
                else playing = false;
                return new DetailedSnapshot(playing, dotaAlive, games, clients, orphans);
            }
            finally { foreach (var p in all) try { p.Dispose(); } catch { } }
        }
        catch { return new DetailedSnapshot(false, false, games, clients, orphans); }
    }

    private static Dictionary<string, List<string>> KnownPathMap(IEnumerable<GameRecord>? knownGames)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (knownGames == null) return map;
        foreach (var g in knownGames)
        {
            if (string.IsNullOrWhiteSpace(g.Name) || string.IsNullOrWhiteSpace(g.Path)) continue;
            if (!map.TryGetValue(g.Name, out var list))
                map[g.Name] = list = new List<string>();
            if (!list.Contains(g.Path, StringComparer.OrdinalIgnoreCase))
                list.Add(g.Path);
        }
        return map;
    }

    private static bool OrphanPathsMatch(string name, IEnumerable<string> livePaths, Dictionary<string, List<string>> knownPaths)
    {
        var lives = livePaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (!knownPaths.TryGetValue(name, out var known) || known.Count == 0)
            return true; // legacy: name-only memory, no path recorded yet
        if (lives.Count == 0)
            return true; // unreadable path: stay conservative, keep counting
        return lives.Any(l => known.Any(k => string.Equals(l, k, StringComparison.OrdinalIgnoreCase)));
    }

    // Playing = Steam client alive, or a game launched by Steam alive, or a
    // previously-seen game exe (orphan after client exit) still alive.
    // Updates seenGames in place with descendant exe names observed right now.
    public static bool IsPlaying(ICollection<string> seenGames) =>
        GetPlaySnapshot(seenGames).Playing;

    public static bool IsDotaRunning() =>
        GetPlaySnapshot(new List<string>()).DotaRunning;

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return ""; }
    }

    private static int SafeId(Process p)
    {
        try { return p.Id; } catch { return -1; }
    }

    private static string SafePath(Process p)
    {
        try { return p.MainModule?.FileName ?? ""; } catch { return ""; }
    }

    private static bool HasSteamAncestor(int pid, HashSet<int> roots, Dictionary<int, int> parentOf)
    {
        var seen = new HashSet<int>();
        int cur = pid;
        while (parentOf.TryGetValue(cur, out int parent) && parent > 0 && seen.Add(cur))
        {
            if (roots.Contains(parent)) return true;
            cur = parent;
        }
        return false;
    }

    private static int Depth(int pid, Dictionary<int, int> parentOf)
    {
        int d = 0, cur = pid;
        while (parentOf.TryGetValue(cur, out int parent) && parent > 0 && d < 64)
        { d++; cur = parent; }
        return d;
    }

    #region Toolhelp32 parent PIDs

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static Dictionary<int, int> BuildParentMap()
    {
        var map = new Dictionary<int, int>();
        IntPtr snap = IntPtr.Zero;
        try
        {
            snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref entry)) return map;
            do
            {
                map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
            } while (Process32Next(snap, ref entry));
        }
        catch { }
        finally { if (snap != IntPtr.Zero) try { CloseHandle(snap); } catch { } }
        return map;
    }

    #endregion
}
