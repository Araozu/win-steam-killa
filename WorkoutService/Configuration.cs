using System.Globalization;

// ---------- config ----------

sealed record WorkoutConfig(
    string StravaClientId,
    string StravaClientSecret,
    string StravaRedirectUri,
    string ApiToken,
    string UserTimeZone,
    string DataDir,
    int PollMinutes,
    double Multiplier,
    long MinWorkoutSeconds,
    long MaxDotaSeconds,
    bool CountManual)
{
    static string Env(string k, string dflt = "") => Environment.GetEnvironmentVariable(k) ?? dflt;

    public static WorkoutConfig FromEnv()
    {
        var config = new WorkoutConfig(
            Env("STRAVA_CLIENT_ID"),
            Env("STRAVA_CLIENT_SECRET"),
            Env("STRAVA_REDIRECT_URI"),
            Env("API_TOKEN"),
            FirstNonEmpty(Env("USER_TIMEZONE"), "UTC"),
            FirstNonEmpty(Env("DATA_DIR"), "/data"),
            int.TryParse(FirstNonEmpty(Env("POLL_INTERVAL_MINUTES"), "5"), out var p) ? Math.Clamp(p, 1, 60) : 5,
            double.TryParse(FirstNonEmpty(Env("WORKOUT_MULTIPLIER"), "4"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : double.NaN,
            long.TryParse(FirstNonEmpty(Env("MIN_WORKOUT_SECONDS"), "600"), out var mn) ? mn : 600,
            long.TryParse(FirstNonEmpty(Env("MAX_DOTA_SECONDS"), "14400"), out var mx) ? mx : 14400,
            bool.TryParse(FirstNonEmpty(Env("COUNT_MANUAL"), "false"), out var cm) && cm);
        if (!double.IsFinite(config.Multiplier) || config.Multiplier < 0
            || config.MinWorkoutSeconds < 0 || config.MaxDotaSeconds < 0)
            throw new InvalidOperationException("Workout multiplier and durations must be finite and nonnegative.");
        _ = ResolveTimeZone(config.UserTimeZone);
        return config;
    }

    static string FirstNonEmpty(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;

    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new InvalidOperationException($"Invalid USER_TIMEZONE: {id}", ex); }
    }
}

static class WorkoutLogic
{
    // Same rule as SteamTimer logical day: day flips at 03:00 local.
    public static string LogicalDayFor(DateTimeOffset utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(utc, tz);
        var date = local.Hour < 3 ? local.Date.AddDays(-1) : local.Date;
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static bool TryDay(string value, out DateOnly day) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    public static long DotaCredit(long workoutSeconds, WorkoutConfig c)
    {
        double credit = Math.Floor(Math.Max(0, workoutSeconds) * c.Multiplier);
        if (c.MaxDotaSeconds > 0) credit = Math.Min(credit, c.MaxDotaSeconds);
        return credit >= long.MaxValue ? long.MaxValue : (long)credit;
    }
}
