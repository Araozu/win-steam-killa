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
            bool found = false;
            foreach (var path in new[] { _file, _file + ".bak" })
            {
                if (!File.Exists(path)) continue;
                found = true;
                try
                {
                    var state = JsonSerializer.Deserialize<TimerState>(File.ReadAllText(path));
                    if (state != null && state.Version != 7) return state; // Engine owns policy migrations.
                    if (state != null && LogicalDay.TryParse(state.LogicalDay, out _)
                        && state.SeenGames != null && state.GameHistory != null
                        && double.IsFinite(state.UsedSeconds) && state.UsedSeconds >= 0
                        && double.IsFinite(state.SessionUsedSeconds) && state.SessionUsedSeconds >= 0
                        && double.IsFinite(state.DotaUsedSeconds) && state.DotaUsedSeconds >= 0)
                        return state;
                }
                catch (JsonException) { }
            }
            if (found) throw new InvalidDataException("SteamTimer state and backup are unreadable; refusing to erase usage.");
            return null;
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
                if (File.Exists(_file)) File.Replace(tmp, _file, _file + ".bak");
                else File.Move(tmp, _file);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var path in new[] { _file, _file + ".bak" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
