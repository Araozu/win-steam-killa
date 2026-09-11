namespace Shared;

// Non-overridable policy. To change the cap you must edit + rebuild.
public static class Limits
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(3);
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(15);
    // Day resets at 03:00 local time.
    public const int ResetHourLocal = 3;
    public const string PipeName = "steamtimer";
    public const string StateDir = @"C:\ProgramData\SteamTimer";
    public const string StateFile = @"C:\ProgramData\SteamTimer\state.json";
}
