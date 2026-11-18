namespace Shared;

// Non-overridable policy. To change budgets you must edit + rebuild.
// Model: every play session is auto-tracked (no manual start). Each session can
// use up to 2h of the day's budget; after every session a cooldown blocks Steam
// equal to that session's length rounded up to 5 minutes.
// Weekday play windows (Mon–Fri 05:30–06:15 + 20:15–22:00 local, weekends free):
// outside the windows Steam is blocked and nothing accrues.
public static class Limits
{
    // Max continuous play session (every day).
    public static readonly TimeSpan SessionMax = TimeSpan.FromHours(2);
    // Cooldown after a session = session length rounded up to this quantum.
    public static readonly TimeSpan CooldownRoundUp = TimeSpan.FromMinutes(5);
    // Max leftover budget carried from one logical day to the next.
    public static readonly TimeSpan MaxCarryover = TimeSpan.FromMinutes(30);
    // Dota 2 weekly play budget (resets Monday 03:00 local with the week).
    public static readonly TimeSpan DotaWeeklyBudget = TimeSpan.FromHours(8);
    // Dota 2 is entirely forbidden on these weekdays (logical day).
    public static readonly HashSet<DayOfWeek> DotaForbiddenDays = new()
    {
        DayOfWeek.Monday,
        DayOfWeek.Wednesday,
        DayOfWeek.Friday,
    };
    // Weekday play windows (local wall-clock). Mon–Fri Steam may only run
    // inside these ranges; Sat/Sun have no time restriction (budget/cooldown/
    // Dota rules still apply). End is exclusive (22:00 means up to 21:59:59).
    public static readonly TimeSpan WeekdayWindow1Start = new(5, 30, 0);
    public static readonly TimeSpan WeekdayWindow1End = new(6, 15, 0);
    public static readonly TimeSpan WeekdayWindow2Start = new(20, 15, 0);
    public static readonly TimeSpan WeekdayWindow2End = new(22, 0, 0);
    public static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(15);
    // Borrow/advance: each press consumes BorrowCost of tomorrow's budget and
    // grants BorrowCost * effectiveness today. First press is 95% effective,
    // each further press subtracts an escalating penalty: -7pp, then -8pp,
    // -9pp, ... floored at 0.1%.
    // Blocked on Sundays, usable repeatedly.
    public static readonly TimeSpan BorrowCost = TimeSpan.FromMinutes(10);
    public const double BorrowFirstEffectiveness = 0.95;
    public const double BorrowBaseRate = 0.07;
    public const double BorrowRateStep = 0.01;
    public const double BorrowMinEffectiveness = 0.001;

    // Dota weekly borrow: same 10m cost and same decay curve as the daily
    // borrow (95% first, -7pp, -8pp, -9pp, ...), but floored at 50% instead
    // of 0.1%. Each press consumes BorrowCost of NEXT week's Dota budget and
    // grants BorrowCost * effectiveness this week. Repeatable, capped so next
    // week never drops below zero.
    public const double DotaBorrowMinEffectiveness = 0.5;

    // 1-indexed press number today (1 = first borrow). Never below the floor.
    public static double EffectivenessForBorrowNumber(int n)
    {
        if (n < 1) n = 1;
        double e = BorrowFirstEffectiveness;
        for (int i = 2; i <= n; i++)
            e -= BorrowBaseRate + BorrowRateStep * (i - 2);
        return Math.Max(BorrowMinEffectiveness, e);
    }

    public static TimeSpan BorrowGainForNumber(int n)
    {
        double e = EffectivenessForBorrowNumber(n);
        int secs = (int)Math.Max(1, Math.Round(BorrowCost.TotalSeconds * e));
        return TimeSpan.FromSeconds(secs);
    }

    public static double NextBorrowEffectiveness(int borrowCountSoFar) =>
        EffectivenessForBorrowNumber(borrowCountSoFar + 1);

    public static string FormatEffectiveness(double e) =>
        e < 0.005 ? "0.1%" : $"{e * 100:0}%";

    // Precise short gain: "9m 30s", "6m", "1m 48s".
    public static string FormatGain(TimeSpan t)
    {
        int totalSecs = Math.Max(0, (int)Math.Round(t.TotalSeconds));
        int m = totalSecs / 60;
        int s = totalSecs % 60;
        return s == 0 ? $"{m}m" : $"{m}m {s:00}s";
    }

    public static string BorrowButtonText(int borrowCountSoFar) =>
        $"Borrow 10m from tomorrow (+{FormatGain(BorrowGainForNumber(borrowCountSoFar + 1))} today)";

    // 1-indexed press number this week (1 = first Dota borrow). Same decay as
    // daily borrow, floored at 50%: 95%, 88%, 80%, 71%, 61%, 50%, 50%, ...
    public static double EffectivenessForDotaBorrowNumber(int n)
    {
        if (n < 1) n = 1;
        double e = BorrowFirstEffectiveness;
        for (int i = 2; i <= n; i++)
            e -= BorrowBaseRate + BorrowRateStep * (i - 2);
        return Math.Max(DotaBorrowMinEffectiveness, e);
    }

    public static TimeSpan DotaBorrowGainForNumber(int n)
    {
        double e = EffectivenessForDotaBorrowNumber(n);
        int secs = (int)Math.Max(1, Math.Round(BorrowCost.TotalSeconds * e));
        return TimeSpan.FromSeconds(secs);
    }

    public static double NextDotaBorrowEffectiveness(int borrowCountSoFar) =>
        EffectivenessForDotaBorrowNumber(borrowCountSoFar + 1);

    public static string DotaBorrowButtonText(int borrowCountSoFar) =>
        $"Borrow 10m from next week (+{FormatGain(DotaBorrowGainForNumber(borrowCountSoFar + 1))} this week)";
    // Day resets at 03:00 local time.
    public const int ResetHourLocal = 3;
    public const string PipeName = "steamtimer";
    public const string StateDir = @"C:\ProgramData\SteamTimer";
    public const string StateFile = @"C:\ProgramData\SteamTimer\state.json";

    // Base daily play budget by weekday of the *logical* day's calendar date.
    // Carryover (up to MaxCarryover of yesterday's leftover) is added on top.
    public static TimeSpan DailyBaseFor(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Saturday => TimeSpan.FromHours(2.5),
        DayOfWeek.Sunday => TimeSpan.FromHours(4),
        _ => TimeSpan.FromMinutes(90), // Mon–Fri
    };

    public static TimeSpan DailyBaseForDate(DateTime date) => DailyBaseFor(date.DayOfWeek);

    // True when this logical day ("yyyy-MM-dd") forbids Dota 2 entirely.
    public static bool IsDotaForbiddenLogicalDay(string logicalDay)
    {
        if (LogicalDay.TryParse(logicalDay, out var d))
            return DotaForbiddenDays.Contains(d.DayOfWeek);
        return false;
    }

    // Cooldown for a finished session: its length rounded up to CooldownRoundUp.
    // Zero/negative input => zero (no session => no cooldown).
    public static TimeSpan CooldownForSession(TimeSpan sessionLen)
    {
        if (sessionLen <= TimeSpan.Zero) return TimeSpan.Zero;
        double units = Math.Ceiling(sessionLen.TotalSeconds / CooldownRoundUp.TotalSeconds);
        return TimeSpan.FromSeconds(units * CooldownRoundUp.TotalSeconds);
    }

    // Monday (yyyy-MM-dd) of the logical week containing this logical day.
    // Week runs Mon 03:00 local -> next Mon 03:00 local.
    public static string WeekStartForLogicalDay(string logicalDay)
    {
        if (LogicalDay.TryParse(logicalDay, out var d))
        {
            int back = ((int)d.DayOfWeek + 6) % 7; // Monday => 0 ... Sunday => 6
            return d.Date.AddDays(-back).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
        return logicalDay;
    }

    // Next Monday 03:00 local (Dota week reset) after localNow.
    public static DateTime NextWeeklyResetLocal(DateTime localNow)
    {
        var logicalToday = localNow.Hour < ResetHourLocal
            ? localNow.Date.AddDays(-1)
            : localNow.Date;
        int back = ((int)logicalToday.DayOfWeek + 6) % 7;
        var monday = logicalToday.AddDays(-back);
        return monday.AddDays(7).AddHours(ResetHourLocal);
    }

    public static string FormatHm(TimeSpan t)
    {
        int mins = (int)Math.Ceiling(t.TotalMinutes);
        return $"{mins / 60}h {mins % 60:00}m";
    }

    public static bool IsWeekend(DayOfWeek dow) =>
        dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday;

    // True when Steam is allowed to run at this local wall-clock moment.
    // Weekends: always true. Weekdays: only inside the two play windows.
    public static bool IsPlayAllowed(DateTime localNow)
    {
        if (IsWeekend(localNow.DayOfWeek)) return true;
        var t = localNow.TimeOfDay;
        if (t >= WeekdayWindow1Start && t < WeekdayWindow1End) return true;
        if (t >= WeekdayWindow2Start && t < WeekdayWindow2End) return true;
        return false;
    }

    // End of the current allowed window (local). Null when unrestricted
    // (weekends) or currently blocked.
    public static DateTime? CurrentWindowEndLocal(DateTime localNow)
    {
        if (IsWeekend(localNow.DayOfWeek)) return null;
        var t = localNow.TimeOfDay;
        var today = localNow.Date;
        if (t >= WeekdayWindow1Start && t < WeekdayWindow1End) return today + WeekdayWindow1End;
        if (t >= WeekdayWindow2Start && t < WeekdayWindow2End) return today + WeekdayWindow2End;
        return null;
    }

    // Next local moment when play becomes allowed. Returns localNow when
    // already allowed.
    public static DateTime NextPlayAllowedLocal(DateTime localNow)
    {
        if (IsPlayAllowed(localNow)) return localNow;
        // Blocked => must be a weekday (weekends never block).
        var today = localNow.Date;
        var t = localNow.TimeOfDay;
        if (t < WeekdayWindow1Start) return today + WeekdayWindow1Start;
        if (t < WeekdayWindow2Start) return today + WeekdayWindow2Start;
        // After the evening window: tomorrow. Weekend starts at midnight,
        // weekdays at the morning window.
        var tomorrow = today.AddDays(1);
        return IsWeekend(tomorrow.DayOfWeek) ? tomorrow : tomorrow + WeekdayWindow1Start;
    }

    public const string PlayHoursSummary = "Mon\u2013Fri 05:30\u201306:15 + 20:15\u201322:00";
    public static string FormatClock(DateTime local) => local.ToString("HH:mm");
}
