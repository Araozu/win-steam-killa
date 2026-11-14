using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

var cfg = WorkoutConfig.FromEnv();
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<ActivityStore>();
builder.Services.AddHttpClient<StravaClient>();
builder.Services.AddHostedService<SyncWorker>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { ok = true, timeUtc = DateTimeOffset.UtcNow }));

// Step 1: point the browser here once to link Strava.
app.MapGet("/auth/login", (WorkoutConfig c) =>
{
    if (string.IsNullOrWhiteSpace(c.StravaClientId) || string.IsNullOrWhiteSpace(c.StravaRedirectUri))
        return Results.Problem("STRAVA_CLIENT_ID / STRAVA_REDIRECT_URI not configured.", statusCode: 500);
    var url = "https://www.strava.com/oauth/authorize"
        + $"?client_id={Uri.EscapeDataString(c.StravaClientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(c.StravaRedirectUri)}"
        + "&response_type=code&scope=activity:read_all";
    return Results.Redirect(url);
});

// Step 2: Strava redirects here with ?code=. Public (no API_TOKEN); exchanges + stores tokens.
app.MapGet("/auth/callback", async (string? code, StravaClient strava) =>
{
    if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest("missing ?code=");
    try
    {
        await strava.ExchangeCodeAsync(code);
        return Results.Ok("Strava linked. You can close this tab.");
    }
    catch (Exception ex) { return Results.Problem("token exchange failed: " + ex.Message, statusCode: 502); }
});

app.MapGet("/auth/status", async (TokenStore tokens) =>
{
    var t = await tokens.LoadAsync();
    if (t is null) return Results.Ok(new { linked = false });
    return Results.Ok(new { linked = true, expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(t.ExpiresAt) });
});

bool Authorized(HttpRequest req, WorkoutConfig c)
{
    if (string.IsNullOrWhiteSpace(c.ApiToken)) return false;
    if (!req.Headers.TryGetValue("Authorization", out var h)) return false;
    return string.Equals(h.ToString(), "Bearer " + c.ApiToken, StringComparison.Ordinal);
}

// Manual trigger (also runs on a timer). Bearer required.
app.MapPost("/api/sync", async (HttpRequest req, WorkoutConfig c, StravaClient strava) =>
{
    if (!Authorized(req, c)) return Results.Unauthorized();
    try
    {
        var s = await strava.SyncAsync();
        return Results.Ok(s);
    }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 409); }
    catch (Exception ex) { return Results.Problem("sync failed: " + ex.Message, statusCode: 502); }
});

// Main endpoint polled by SteamTimer: GET /api/credit?day=yyyy-MM-dd (Bearer required).
// day = SteamTimer logical day (03:00 reset). Omitted => current logical day in USER_TIMEZONE.
// Returns workoutSeconds (sum of qualifying moving_time) + dotaSeconds = workout * multiplier (capped).
app.MapGet("/api/credit", async (HttpRequest req, string? day, WorkoutConfig c, ActivityStore store) =>
{
    if (!Authorized(req, c)) return Results.Unauthorized();
    var tz = WorkoutConfig.ResolveTimeZone(c.UserTimeZone);
    string logicalDay;
    if (!string.IsNullOrWhiteSpace(day))
    {
        if (!DateOnly.TryParse(day, out _)) return Results.BadRequest("day must be yyyy-MM-dd");
        logicalDay = day;
    }
    else logicalDay = WorkoutLogic.LogicalDayFor(DateTimeOffset.UtcNow, tz);

    var snap = await store.LoadAsync();
    var qualifying = new List<object>();
    long workoutSeconds = 0;
    if (snap is not null)
    {
        foreach (var a in snap.Activities)
        {
            var aday = WorkoutLogic.LogicalDayFor(a.StartUtc, tz);
            if (!string.Equals(aday, logicalDay, StringComparison.Ordinal)) continue;
            if (a.Manual && !c.CountManual) continue;
            if (a.MovingTime < c.MinWorkoutSeconds) continue;
            workoutSeconds += a.MovingTime;
            qualifying.Add(new { id = a.Id, name = a.Name, type = a.SportType ?? a.Type, startUtc = a.StartUtc, movingTime = a.MovingTime, manual = a.Manual });
        }
    }
    long dotaSeconds = (long)Math.Floor(workoutSeconds * c.Multiplier);
    if (c.MaxDotaSeconds > 0) dotaSeconds = Math.Min(dotaSeconds, c.MaxDotaSeconds);

    return Results.Ok(new
    {
        day = logicalDay,
        workoutSeconds,
        dotaSeconds,
        multiplier = c.Multiplier,
        activityCount = qualifying.Count,
        updatedAtUtc = snap?.UpdatedAtUtc,
        activities = qualifying,
    });
});

// Debug: raw cached activities in a logical-day range. Bearer required.
app.MapGet("/api/activities", async (HttpRequest req, string? from, string? to, WorkoutConfig c, ActivityStore store) =>
{
    if (!Authorized(req, c)) return Results.Unauthorized();
    var snap = await store.LoadAsync();
    var list = snap?.Activities ?? new();
    if (DateOnly.TryParse(from, out var f)) list = list.Where(a => a.StartUtc >= f.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToList();
    if (DateOnly.TryParse(to, out var t)) list = list.Where(a => a.StartUtc < t.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToList();
    return Results.Ok(new { count = list.Count, updatedAtUtc = snap?.UpdatedAtUtc, activities = list });
});

app.Run();

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

    public static WorkoutConfig FromEnv() => new(
        Env("STRAVA_CLIENT_ID"),
        Env("STRAVA_CLIENT_SECRET"),
        Env("STRAVA_REDIRECT_URI"),
        Env("API_TOKEN"),
        FirstNonEmpty(Env("USER_TIMEZONE"), "UTC"),
        FirstNonEmpty(Env("DATA_DIR"), "/data"),
        int.TryParse(FirstNonEmpty(Env("POLL_INTERVAL_MINUTES"), "5"), out var p) ? Math.Clamp(p, 1, 60) : 5,
        double.TryParse(FirstNonEmpty(Env("WORKOUT_MULTIPLIER"), "4"), out var m) ? m : 4.0,
        long.TryParse(FirstNonEmpty(Env("MIN_WORKOUT_SECONDS"), "600"), out var mn) ? mn : 600,
        long.TryParse(FirstNonEmpty(Env("MAX_DOTA_SECONDS"), "14400"), out var mx) ? mx : 14400,
        bool.TryParse(FirstNonEmpty(Env("COUNT_MANUAL"), "false"), out var cm) && cm);

    static string FirstNonEmpty(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;

    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }
}

static class WorkoutLogic
{
    // Same rule as SteamTimer logical day: day flips at 03:00 local.
    public static string LogicalDayFor(DateTimeOffset utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(utc, tz);
        var date = local.Hour < 3 ? local.Date.AddDays(-1) : local.Date;
        return date.ToString("yyyy-MM-dd");
    }
}

// ---------- storage (JSON files under DATA_DIR, single-user) ----------

sealed record StravaToken(string AccessToken, string RefreshToken, long ExpiresAt);

sealed class TokenStore
{
    private readonly WorkoutConfig _c;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public TokenStore(WorkoutConfig c) => _c = c;
    string Path => System.IO.Path.Combine(Dir(), "strava_token.json");
    string Dir()
    {
        var d = string.IsNullOrWhiteSpace(_c.DataDir) ? "./data" : _c.DataDir;
        Directory.CreateDirectory(d);
        return d;
    }
    public async Task<StravaToken?> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(Path)) return null;
            var json = await File.ReadAllTextAsync(Path);
            return JsonSerializer.Deserialize<StravaToken>(json);
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(StravaToken t)
    {
        await _gate.WaitAsync();
        try { await File.WriteAllTextAsync(Path, JsonSerializer.Serialize(t)); }
        finally { _gate.Release(); }
    }
}

sealed record CachedActivity(
    long Id,
    string Name,
    string Type,
    string? SportType,
    DateTimeOffset StartUtc,
    long MovingTime,
    bool Manual);

sealed record ActivitySnap(DateTimeOffset UpdatedAtUtc, List<CachedActivity> Activities);

sealed class ActivityStore
{
    private readonly WorkoutConfig _c;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public ActivityStore(WorkoutConfig c) => _c = c;
    string Path => System.IO.Path.Combine(Dir(), "activities.json");
    string Dir()
    {
        var d = string.IsNullOrWhiteSpace(_c.DataDir) ? "./data" : _c.DataDir;
        Directory.CreateDirectory(d);
        return d;
    }
    public async Task<ActivitySnap?> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(Path)) return null;
            var json = await File.ReadAllTextAsync(Path);
            return JsonSerializer.Deserialize<ActivitySnap>(json);
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(ActivitySnap s)
    {
        await _gate.WaitAsync();
        try { await File.WriteAllTextAsync(Path, JsonSerializer.Serialize(s)); }
        finally { _gate.Release(); }
    }
}

// ---------- strava ----------

sealed class StravaClient
{
    private readonly HttpClient _http;
    private readonly WorkoutConfig _c;
    private readonly TokenStore _tokens;
    private readonly ActivityStore _store;
    private readonly ILogger<StravaClient> _log;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public StravaClient(HttpClient http, WorkoutConfig c, TokenStore tokens, ActivityStore store, ILogger<StravaClient> log)
    {
        _http = http; _c = c; _tokens = tokens; _store = store; _log = log;
    }

    public async Task ExchangeCodeAsync(string code)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _c.StravaClientId,
            ["client_secret"] = _c.StravaClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
        });
        var resp = await _http.PostAsync("https://www.strava.com/oauth/token", form);
        resp.EnsureSuccessStatusCode();
        var tok = await resp.Content.ReadFromJsonAsync<StravaTokenResponse>(JsonOpts)
            ?? throw new InvalidOperationException("empty token response");
        await _tokens.SaveAsync(new StravaToken(tok.AccessToken, tok.RefreshToken, tok.ExpiresAt));
    }

    async Task<string> AccessTokenAsync()
    {
        var t = await _tokens.LoadAsync() ?? throw new InvalidOperationException("Strava not linked. Visit /auth/login first.");
        if (t.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds() > 300) return t.AccessToken;
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _c.StravaClientId,
            ["client_secret"] = _c.StravaClientSecret,
            ["refresh_token"] = t.RefreshToken,
            ["grant_type"] = "refresh_token",
        });
        var resp = await _http.PostAsync("https://www.strava.com/oauth/token", form);
        resp.EnsureSuccessStatusCode();
        var tok = await resp.Content.ReadFromJsonAsync<StravaTokenResponse>(JsonOpts)
            ?? throw new InvalidOperationException("empty refresh response");
        await _tokens.SaveAsync(new StravaToken(tok.AccessToken, tok.RefreshToken, tok.ExpiresAt));
        return tok.AccessToken;
    }

    public async Task<object> SyncAsync()
    {
        var access = await AccessTokenAsync();
        var after = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        var all = new Dictionary<long, CachedActivity>();
        var existing = await _store.LoadAsync();
        if (existing is not null) foreach (var a in existing.Activities) all[a.Id] = a;

        for (int page = 1; page <= 5; page++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.strava.com/api/v3/athlete/activities?per_page=100&page={page}&after={after}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var acts = await resp.Content.ReadFromJsonAsync<List<StravaActivity>>(JsonOpts) ?? new();
            foreach (var a in acts)
                all[a.Id] = new CachedActivity(a.Id, a.Name ?? "", a.Type ?? "", a.SportType,
                    DateTimeOffset.Parse(a.StartDate), a.MovingTime, a.Manual);
            if (acts.Count < 100) break;
        }
        var snap = new ActivitySnap(DateTimeOffset.UtcNow, all.Values.OrderByDescending(a => a.StartUtc).ToList());
        await _store.SaveAsync(snap);
        _log.LogInformation("Strava sync: {N} activities cached.", snap.Activities.Count);
        return new { activities = snap.Activities.Count, updatedAtUtc = snap.UpdatedAtUtc };
    }

    sealed record StravaTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_at")] long ExpiresAt);

    sealed record StravaActivity(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("sport_type")] string? SportType,
        [property: JsonPropertyName("start_date")] string StartDate,
        [property: JsonPropertyName("moving_time")] long MovingTime,
        [property: JsonPropertyName("manual")] bool Manual);
}

sealed class SyncWorker : BackgroundService
{
    private readonly StravaClient _strava;
    private readonly WorkoutConfig _c;
    private readonly ILogger<SyncWorker> _log;
    public SyncWorker(StravaClient strava, WorkoutConfig c, ILogger<SyncWorker> log)
    { _strava = strava; _c = c; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay first run so /auth/callback can be completed after deploy.
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_c.PollMinutes));
        do
        {
            try { await _strava.SyncAsync(); }
            catch (InvalidOperationException ex) { _log.LogWarning("sync skipped: {M}", ex.Message); }
            catch (Exception ex) { _log.LogWarning(ex, "strava sync failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
