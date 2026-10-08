using System.Text.Json;

namespace App;

// Persists manual UI preferences separately from the coin wallet and the
// service state, so hiding Flight Club never touches coins or time budgets.
internal sealed record AppSettingsState
{
    public int Version { get; init; } = 1;
    public bool FlightClubVisible { get; init; } = true;
}

internal sealed class AppSettings
{
    private readonly string _path;
    private AppSettingsState _state;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool FlightClubVisible => _state.FlightClubVisible;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamTimer", "app-settings.json");

    public AppSettings() : this(DefaultPath) { }

    public AppSettings(string path)
    {
        _path = path;
        _state = Load(path);
    }

    private static AppSettingsState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettingsState();
            var state = JsonSerializer.Deserialize<AppSettingsState>(File.ReadAllText(path));
            if (state == null || state.Version != 1) return new AppSettingsState();
            return state;
        }
        catch (IOException) { return new AppSettingsState(); }
        catch (UnauthorizedAccessException) { return new AppSettingsState(); }
        catch (JsonException) { return new AppSettingsState(); }
    }

    public void SetFlightClubVisible(bool visible)
    {
        if (_state.FlightClubVisible == visible) return;
        Save(_state with { FlightClubVisible = visible });
    }

    private void Save(AppSettingsState next)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        string temp = _path + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, next, JsonOptions);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
            _state = next;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
