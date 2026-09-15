namespace Shared;

// Non-overridable policy. To change budgets you must edit + rebuild.
public static class Limits
{
    // Absolute parser ceiling (Sunday budget). Manual caps can never exceed this.
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(3);
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(15);
    // Day resets at 03:00 local time.
    public const int ResetHourLocal = 3;
    public const string PipeName = "steamtimer";
    public const string StateDir = @"C:\ProgramData\SteamTimer";
    public const string StateFile = @"C:\ProgramData\SteamTimer\state.json";

    // Daily play budget by weekday of the *logical* day's calendar date.
    public static TimeSpan DailyBudgetFor(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Saturday => TimeSpan.FromHours(2.5),
        DayOfWeek.Sunday => TimeSpan.FromHours(3),
        _ => TimeSpan.FromMinutes(90), // Mon–Fri
    };

    public static TimeSpan DailyBudgetForDate(DateTime date) => DailyBudgetFor(date.DayOfWeek);

    public static string FormatHm(TimeSpan t)
    {
        int mins = (int)Math.Ceiling(t.TotalMinutes);
        return $"{mins / 60}h {mins % 60:00}m";
    }
}
