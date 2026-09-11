using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Service;

// Kills Steam client processes + any descendant (games launched via Steam).
// No NuGet needed: parent PIDs via Toolhelp32 snapshot (kernel32).
public static class Killer
{
    public static readonly string[] SteamNames =
        ["steam", "steamwebhelper", "gameoverlayui", "steamservice"];

    public static bool IsSteamName(string processName) =>
        SteamNames.Contains(processName, StringComparer.OrdinalIgnoreCase);

    public static int KillAll(ILogger? log = null)
    {
        int killed = 0;
        try
        {
            var all = Process.GetProcesses();
            var byId = all.ToDictionary(p => SafeId(p), p => p);
            var parentOf = BuildParentMap();
            var steamRoots = new HashSet<int>(
                all.Where(p => IsSteamName(SafeName(p))).Select(SafeId).Where(id => id > 0));

            if (steamRoots.Count == 0) return 0;

            // Any process whose ancestor chain hits a steam root is in scope.
            var targets = new List<Process>();
            foreach (var p in all)
            {
                int id = SafeId(p);
                if (id <= 0) continue;
                if (steamRoots.Contains(id) || HasSteamAncestor(id, steamRoots, parentOf))
                    targets.Add(p);
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
