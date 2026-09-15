using System.Diagnostics;
using System.Runtime.InteropServices;

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

    public static bool IsSteamName(string processName) =>
        SteamNames.Contains(processName, StringComparer.OrdinalIgnoreCase);

    public static int KillAll(ILogger? log = null) => KillPlaySet(null, log);

    // Kill Steam subtree plus any extra exe names (orphaned games whose Steam
    // parent already exited). extraExeNames are bare process names, any case.
    public static int KillPlaySet(IEnumerable<string>? extraExeNames, ILogger? log = null)
    {
        int killed = 0;
        try
        {
            var all = Process.GetProcesses();
            var byId = all.ToDictionary(p => SafeId(p), p => p);
            var parentOf = BuildParentMap();
            var steamRoots = new HashSet<int>(
                all.Where(p => IsSteamName(SafeName(p))).Select(SafeId).Where(id => id > 0));

            var extra = new HashSet<string>(
                extraExeNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            // Any process whose ancestor chain hits a steam root is in scope,
            // plus any known orphaned game exe still running.
            var targets = new List<Process>();
            foreach (var p in all)
            {
                int id = SafeId(p);
                if (id <= 0) continue;
                if (steamRoots.Contains(id) || HasSteamAncestor(id, steamRoots, parentOf))
                    targets.Add(p);
                else if (extra.Count > 0 && extra.Contains(SafeName(p)))
                    targets.Add(p);
            }
            if (targets.Count == 0)
            {
                foreach (var p in all) try { p.Dispose(); } catch { }
                return 0;
            }
            // Kill leaves first (games), roots last.
            targets.Sort((a, b) =>
                Depth(SafeId(b), parentOf).CompareTo(Depth(SafeId(a), parentOf)));

            foreach (var p in targets)
            {
                try
                {
                    string name = SafeName(p);
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
                finally { try { p.Dispose(); } catch { } }
            }
            foreach (var p in all)
            {
                if (targets.Contains(p)) continue;
                try { p.Dispose(); } catch { }
            }
        }
        catch (Exception ex) { log?.LogWarning(ex, "KillAll failed"); }
        return killed;
    }

    public static bool AnySteamRunning()
    {
        try
        {
            return Process.GetProcesses().Any(p => IsSteamName(SafeName(p)));
        }
        catch { return false; }
    }

    public static bool IsClientName(string processName) =>
        ClientNames.Contains(processName, StringComparer.OrdinalIgnoreCase);

    // Playing = Steam client alive, or a game launched by Steam alive, or a
    // previously-seen game exe (orphan after client exit) still alive.
    // Updates seenGames in place with descendant exe names observed right now.
    public static bool IsPlaying(ICollection<string> seenGames)
    {
        try
        {
            var all = Process.GetProcesses();
            try
            {
                var parentOf = BuildParentMap();
                var clientRoots = new HashSet<int>(
                    all.Where(p => IsClientName(SafeName(p))).Select(SafeId).Where(id => id > 0));
                bool anyClient = clientRoots.Count > 0;
                var descendants = new List<string>();
                foreach (var p in all)
                {
                    int id = SafeId(p);
                    if (id <= 0 || clientRoots.Contains(id)) continue;
                    if (HasSteamAncestor(id, clientRoots, parentOf))
                    {
                        string n = SafeName(p);
                        if (!string.IsNullOrEmpty(n) && !IsClientName(n) && !IsSteamName(n))
                            descendants.Add(n);
                    }
                }
                foreach (var n in descendants.Distinct(StringComparer.OrdinalIgnoreCase))
                    if (!seenGames.Contains(n, StringComparer.OrdinalIgnoreCase))
                        seenGames.Add(n);
                if (anyClient || descendants.Count > 0) return true;
                if (seenGames.Count > 0)
                {
                    var names = new HashSet<string>(all.Select(SafeName), StringComparer.OrdinalIgnoreCase);
                    if (seenGames.Any(g => names.Contains(g))) return true;
                }
                return false;
            }
            finally { foreach (var p in all) try { p.Dispose(); } catch { } }
        }
        catch { return false; }
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return ""; }
    }

    private static int SafeId(Process p)
    {
        try { return p.Id; } catch { return -1; }
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
