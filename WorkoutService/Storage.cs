using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

// ---------- storage (JSON files under DATA_DIR, single-user) ----------

sealed record StravaToken(string AccessToken, string RefreshToken, long ExpiresAt);

sealed class TokenStore
{
    private readonly WorkoutConfig _c;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public TokenStore(WorkoutConfig c) => _c = c;
    string Path => System.IO.Path.Combine(Dir(), "strava_token.json");
    string Dir()
    {
        var d = string.IsNullOrWhiteSpace(_c.DataDir) ? "./data" : _c.DataDir;
        Directory.CreateDirectory(d);
        return d;
    }
    public async Task<StravaToken?> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(Path)) return null;
            var json = await File.ReadAllTextAsync(Path);
            return JsonSerializer.Deserialize<StravaToken>(json);
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(StravaToken t)
    {
        await _gate.WaitAsync();
        try { await AtomicJson.SaveAsync(Path, t); }
        finally { _gate.Release(); }
    }
}

sealed record CachedActivity(
    long Id,
    string Name,
    string Type,
    string? SportType,
    DateTimeOffset StartUtc,
    long MovingTime,
    bool Manual);

sealed record ActivitySnap(DateTimeOffset UpdatedAtUtc, List<CachedActivity> Activities);

sealed class ActivityStore
{
    private readonly WorkoutConfig _c;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public ActivityStore(WorkoutConfig c) => _c = c;
    string Path => System.IO.Path.Combine(Dir(), "activities.json");
    string Dir()
    {
        var d = string.IsNullOrWhiteSpace(_c.DataDir) ? "./data" : _c.DataDir;
        Directory.CreateDirectory(d);
        return d;
    }
    public async Task<ActivitySnap?> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(Path)) return null;
            var json = await File.ReadAllTextAsync(Path);
            return JsonSerializer.Deserialize<ActivitySnap>(json);
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(ActivitySnap s)
    {
        await _gate.WaitAsync();
        try { await AtomicJson.SaveAsync(Path, s); }
        finally { _gate.Release(); }
    }
}

static class AtomicJson
{
    public static async Task SaveAsync<T>(string path, T value)
    {
        var temp = path + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await JsonSerializer.SerializeAsync(file, value);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
