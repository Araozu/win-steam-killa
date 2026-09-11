using System.Text.RegularExpressions;

namespace Shared;

public static class DurationParser
{
    // Accepts: 1h, 1h30m, 1H30, 90m, 2h, 45m, 3600, 3600s, 1:30, 1:30:00, "1 h 30 m"
    public static bool TryParse(string? input, out TimeSpan duration, out string error)
    {
        duration = TimeSpan.Zero;
        error = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter something like 1h, 1h30m or 90m.";
            return false;
        }
        var s = input.Trim().ToLowerInvariant().Replace(" ", "");

        // H:MM[:SS] form
        var colon = Regex.Match(s, @"^(\d+):(\d{1,2})(?::(\d{1,2}))?$");
        if (colon.Success)
        {
            int h = int.Parse(colon.Groups[1].Value);
            int m = int.Parse(colon.Groups[2].Value);
            int sec = colon.Groups[3].Success ? int.Parse(colon.Groups[3].Value) : 0;
            if (m >= 60 || sec >= 60) { error = "Minutes/seconds must be < 60."; return false; }
            duration = new TimeSpan(h, m, sec);
            return CheckBounds(ref duration, out error);
        }

        // Number + unit form
        var m2 = Regex.Match(s, @"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s?)?$");
        // Bare number = seconds? Treat bare number as minutes if < 1000 else seconds is confusing.
        // Decide: bare number = minutes (90 -> 90m). Simple for a noob UI.
        if (Regex.IsMatch(s, @"^\d+$"))
        {
            duration = TimeSpan.FromMinutes(int.Parse(s));
            return CheckBounds(ref duration, out error);
        }
        if (!m2.Success || (m2.Groups[1].Value == "" && m2.Groups[2].Value == "" && m2.Groups[3].Value == ""))
        {
            error = "Use e.g. 1h, 1h30m, 2h, 90m or 1:30.";
            return false;
        }
        // Guard: trailing digits without unit only valid as seconds if an h/m part exists, e.g. 1h30
        bool hasHOrM = m2.Groups[1].Value != "" || m2.Groups[2].Value != "";
        string full = m2.Groups[0].Value;
        // If input was like "1h30" the regex reads 30 as seconds; remap to minutes.
        if (hasHOrM && Regex.IsMatch(s, @"h\d+$") && !s.EndsWith("m") && !s.EndsWith("s") && !s.EndsWith("h"))
        {
            var tail = Regex.Match(s, @"h(\d+)$");
            int tailMin = int.Parse(tail.Groups[1].Value);
            int h = m2.Groups[1].Value == "" ? 0 : int.Parse(m2.Groups[1].Value);
            duration = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(tailMin);
            return CheckBounds(ref duration, out error);
        }
        int hh = m2.Groups[1].Value == "" ? 0 : int.Parse(m2.Groups[1].Value);
        int mm = m2.Groups[2].Value == "" ? 0 : int.Parse(m2.Groups[2].Value);
        int ss = m2.Groups[3].Value == "" ? 0 : int.Parse(m2.Groups[3].Value);
        _ = full;
        duration = new TimeSpan(hh, mm, ss);
        return CheckBounds(ref duration, out error);
    }

    private static bool CheckBounds(ref TimeSpan d, out string error)
    {
        error = "";
        if (d < Limits.MinDuration) { error = $"Minimum is {(int)Limits.MinDuration.TotalMinutes}m."; return false; }
        if (d > Limits.MaxDuration) { error = $"Maximum is {(int)Limits.MaxDuration.TotalHours}h (non-overridable)."; return false; }
        return true;
    }
}
