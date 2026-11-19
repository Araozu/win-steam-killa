namespace Shared;

// A logical day runs 00:00 -> 00:00 on the policy clock (UTC-05).
public static class LogicalDay
{
    public static bool TryParse(string value, out DateTime date) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date);

    // policyTime is a wall-clock reading on PolicyClock.
    public static string For(DateTime policyTime)
    {
        var day = policyTime.Hour < Limits.ResetHour
            ? policyTime.Date.AddDays(-1)
            : policyTime.Date;
        return day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string ForUtc(DateTime utc) => For(PolicyClock.FromUtc(utc));

    public static DateTime NextReset(DateTime policyTime)
    {
        var todayReset = policyTime.Date.AddHours(Limits.ResetHour);
        return policyTime < todayReset ? todayReset : todayReset.AddDays(1);
    }
}
