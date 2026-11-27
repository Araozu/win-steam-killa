namespace Shared;

// Every policy rule (day and week resets, play windows, clock times shown to
// the user) runs on a fixed UTC-05 clock. The PC's time zone and DST settings
// do not change when a day starts or which play window applies.
public static class PolicyClock
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(-5);
    public const string Label = "UTC-05";

    // The input is treated as UTC whatever its Kind says.
    public static DateTime FromUtc(DateTime utc) => new(utc.Ticks + Offset.Ticks, DateTimeKind.Unspecified);
    public static DateTime ToUtc(DateTime policyTime) => new(policyTime.Ticks - Offset.Ticks, DateTimeKind.Utc);
    public static DateTime Now => FromUtc(DateTime.UtcNow);
}
