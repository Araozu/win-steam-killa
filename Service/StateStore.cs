using System.Text.Json;
using Shared;

namespace Service;

public class StateStore
{
    private readonly string _dir;
    private readonly string _file;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public StateStore(string? path = null)
    {
        _file = path ?? Limits.StateFile;
        _dir = Path.GetDirectoryName(Path.GetFullPath(_file))!;
    }

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
            try
            {
                using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, state, JsonOpts);
                    file.Flush(flushToDisk: true);
                }
                File.Move(tmp, _file, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
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
