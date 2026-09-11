namespace Shared;

// Logical day runs 03:00 local -> 03:00 local next day.
public static class LogicalDay
{
    public static string ForLocal(DateTime localNow)
    {
        var day = localNow.Hour < Limits.ResetHourLocal
            ? localNow.Date.AddDays(-1)
            : localNow.Date;
        return day.ToString("yyyy-MM-dd");
    }

    public static DateTime NextResetLocal(DateTime localNow)
    {
        var todayReset = localNow.Date.AddHours(Limits.ResetHourLocal);
        return localNow < todayReset ? todayReset : todayReset.AddDays(1);
    }
}
