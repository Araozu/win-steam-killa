using System.Text.Json;
using Shared;

namespace Service;

public class StateStore
{
    private readonly string _dir = Limits.StateDir;
    private readonly string _file = Limits.StateFile;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public TimerState? Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_file)) return null;
                var json = File.ReadAllText(_file);
                return JsonSerializer.Deserialize<TimerState>(json);
            }
            catch { return null; }
        }
    }

    public void Save(TimerState state)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_dir);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
            File.Move(tmp, _file, overwrite: true);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            try { if (File.Exists(_file)) File.Delete(_file); } catch { }
        }
    }
}
