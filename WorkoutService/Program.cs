using System.Globalization;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16 * 1024);
var cfg = WorkoutConfig.FromEnv();
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<ActivityStore>();
builder.Services.AddSingleton<OAuthLoginState>();
builder.Services.AddHttpClient("strava", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton(sp => new StravaClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("strava"), cfg,
    sp.GetRequiredService<TokenStore>(), sp.GetRequiredService<ActivityStore>(),
    sp.GetRequiredService<ILogger<StravaClient>>()));
builder.Services.AddHostedService<SyncWorker>();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { ok = true, timeUtc = DateTimeOffset.UtcNow }));

IResult BeginLogin(HttpContext context, WorkoutConfig c, OAuthLoginState states)
{
    if (string.IsNullOrWhiteSpace(c.StravaClientId) || string.IsNullOrWhiteSpace(c.StravaClientSecret)
        || !Uri.TryCreate(c.StravaRedirectUri, UriKind.Absolute, out var redirect))
        return Results.Problem("Strava client and redirect URI must be configured.", statusCode: 500);
    var state = states.Create();
    context.Response.Cookies.Append(OAuthLoginState.CookieName, state, new CookieOptions
    {
        HttpOnly = true, Secure = redirect.Scheme == "https", SameSite = SameSiteMode.Lax,
        Path = "/auth/callback", MaxAge = TimeSpan.FromMinutes(10),
    });
    var url = "https://www.strava.com/oauth/authorize"
        + $"?client_id={Uri.EscapeDataString(c.StravaClientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(c.StravaRedirectUri)}"
        + "&response_type=code&scope=activity:read_all"
        + $"&state={state}";
    return Results.Redirect(url);
}

app.MapGet("/auth/login", (HttpContext context, WorkoutConfig c, OAuthLoginState states) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; form-action 'self'; frame-ancestors 'none'";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (Authorized(context.Request, c)) return BeginLogin(context, c, states);
    return Results.Content("""
        <!doctype html><html lang="en"><meta charset="utf-8"><title>Link Strava</title>
        <h1>Link Strava</h1><form method="post" action="/auth/login">
        <label>Workout API token <input name="token" type="password" required autocomplete="off"></label>
        <button type="submit">Continue to Strava</button></form></html>
        """, "text/html");
});
app.MapPost("/auth/login", async (HttpContext context, WorkoutConfig c, OAuthLoginState states) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!context.Request.HasFormContentType) return Results.BadRequest("Expected a form.");
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    if (!TokenMatches(form["token"].ToString(), c.ApiToken)) return Results.Unauthorized();
    return BeginLogin(context, c, states);
});
app.MapGet("/auth/callback", async (HttpContext context, string? code, string? state, string? scope,
    OAuthLoginState states, StravaClient strava, ILogger<StravaClient> log) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    var cookie = context.Request.Cookies[OAuthLoginState.CookieName];
    context.Response.Cookies.Delete(OAuthLoginState.CookieName, new CookieOptions { Path = "/auth/callback" });
    if (!states.Consume(state, cookie)) return Results.BadRequest("Invalid or expired OAuth state. Start linking again.");
    if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest("Strava authorization was not completed.");
    if (scope is null || !scope.Split(',').Contains("activity:read_all", StringComparer.Ordinal))
        return Results.BadRequest("The activity:read_all permission is required. Start linking again.");
    try
    {
        await strava.ExchangeCodeAsync(code, context.RequestAborted);
        return Results.Ok("Strava linked. You can close this tab.");
    }
    catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
    {
        log.LogWarning(ex, "Strava token exchange failed");
        return Results.Problem("Strava token exchange failed.", statusCode: 502);
    }
});
app.MapGet("/auth/status", async (TokenStore tokens) =>
{
    var t = await tokens.LoadAsync();
    return Results.Ok(new { linked = t is not null, expiresAtUtc = t is null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds(t.ExpiresAt) });
});

bool TokenMatches(string provided, string expected) => !string.IsNullOrWhiteSpace(expected)
    && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
bool Authorized(HttpRequest req, WorkoutConfig c) =>
    req.Headers.TryGetValue("Authorization", out var h) && h.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
    && TokenMatches(h.ToString()[7..], c.ApiToken);

app.MapPost("/api/sync", async (HttpRequest req, WorkoutConfig c, StravaClient strava, ILogger<StravaClient> log) =>
{
    if (!Authorized(req, c)) return Results.Unauthorized();
    try { return Results.Ok(await strava.SyncAsync(req.HttpContext.RequestAborted)); }
    catch (InvalidOperationException) { return Results.Problem("Strava is not linked.", statusCode: 409); }
    catch (Exception ex) when (ex is not OperationCanceledException || !req.HttpContext.RequestAborted.IsCancellationRequested)
    {
        log.LogWarning(ex, "Strava sync failed");
        return Results.Problem("Strava sync failed.", statusCode: 502);
    }
});

app.MapGet("/api/credit", async (HttpRequest req, string? day, WorkoutConfig c, ActivityStore store) =>
{
    if (!Authorized(req, c)) return Results.Unauthorized();
    var tz = WorkoutConfig.ResolveTimeZone(c.UserTimeZone);
    if (day is not null && !WorkoutLogic.TryDay(day, out _)) return Results.BadRequest("day must be yyyy-MM-dd");
    var logicalDay = day ?? WorkoutLogic.LogicalDayFor(DateTimeOffset.UtcNow, tz);
    var snap = await store.LoadAsync();
    var qualifying = (snap?.Activities ?? []).Where(a =>
        WorkoutLogic.LogicalDayFor(a.StartUtc, tz) == logicalDay
        && (!a.Manual || c.CountManual) && a.MovingTime > 0 && a.MovingTime >= c.MinWorkoutSeconds).ToList();
    long workoutSeconds = qualifying.Sum(a => a.MovingTime);
    long dotaSeconds = WorkoutLogic.DotaCredit(workoutSeconds, c);
    return Results.Ok(new
    {
        day = logicalDay, workoutSeconds, dotaSeconds, multiplier = c.Multiplier,
        activityCount = qualifying.Count, updatedAtUtc = snap?.UpdatedAtUtc,
        activities = qualifying.Select(a => new { id = a.Id, name = a.Name, type = a.SportType ?? a.Type,
            startUtc = a.StartUtc, movingTime = a.MovingTime, manual = a.Manual }),
    });
});

app.MapGet("/api/activities", async (HttpRequest req, string? from, string? to, WorkoutConfig c, ActivityStore store) =>
{
    if (!Authorized(req, c)) return Results.Unauthorized();
    if (from is not null && !WorkoutLogic.TryDay(from, out _) || to is not null && !WorkoutLogic.TryDay(to, out _)
        || from is not null && to is not null && string.CompareOrdinal(from, to) > 0)
        return Results.BadRequest("from and to must be yyyy-MM-dd, with from <= to");
    var tz = WorkoutConfig.ResolveTimeZone(c.UserTimeZone);
    var snap = await store.LoadAsync();
    var list = (snap?.Activities ?? []).Where(a =>
    {
        var day = WorkoutLogic.LogicalDayFor(a.StartUtc, tz);
        return (from is null || string.CompareOrdinal(day, from) >= 0)
            && (to is null || string.CompareOrdinal(day, to) <= 0);
    }).ToList();
    return Results.Ok(new { count = list.Count, updatedAtUtc = snap?.UpdatedAtUtc, activities = list });
});
app.Run();

public partial class Program { }
