using Shared;

namespace App;

internal enum TrayLevel { Unknown, Normal, Low, Blocked }

// The tray tooltip and icon, computed in one place from the window's view
// state. No UI dependencies, so it is covered by the cross-platform tests.
internal static class TrayStatus
{
    public static (string Text, TrayLevel Level) Describe(TimerViewState state, DateTime policyNow)
    {
        if (!state.ServiceAvailable) return (state.Problem, TrayLevel.Unknown);
        if (state.Last is not { } r) return ("Steam Timer — waiting for the service", TrayLevel.Unknown);
        if (!state.IsCurrent) return ("Service status is stale — last known values", TrayLevel.Unknown);

        var values = state.Project(policyNow);
        if (r.OutsidePlayHours || !Limits.IsPlayAllowed(policyNow))
            return ($"Outside play hours — back {Limits.NextPlayAllowedLocal(policyNow):HH:mm}", TrayLevel.Blocked);
        if (r.Enforcing || values.Remaining <= 0)
            return ($"Steam blocked until {Limits.ResetClockText}", TrayLevel.Blocked);
        if (r.InCooldown)
            return ($"Cooldown: {TimeText.Hm(values.CooldownRemaining)} left", TrayLevel.Blocked);

        string text = (values.SessionActive ? $"Playing: {TimeText.Hm(values.Playable)} left" : "Ready")
            + (r.WindowEndsLocal is { } end ? $" • until {end:HH:mm}" : "")
            + (r.DotaForbidden ? " • no Dota today" : r.DotaEnforcing ? " • Dota done" : "");
        var level = values.Playable <= 0 ? TrayLevel.Blocked
            : values.Playable <= TimeSpan.FromMinutes(30).TotalSeconds ? TrayLevel.Low
            : TrayLevel.Normal;
        return (text, level);
    }
}

internal static class TimeText
{
    public static string Hm(double totalSeconds)
    {
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds)) return "—";
        if (totalSeconds < 0) totalSeconds = 0;
        if (totalSeconds > TimeSpan.MaxValue.TotalSeconds) totalSeconds = TimeSpan.MaxValue.TotalSeconds;
        var t = TimeSpan.FromSeconds(totalSeconds);
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }
}
