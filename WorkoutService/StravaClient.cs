using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

// ---------- strava ----------

sealed class StravaClient
{
    private readonly HttpClient _http;
    private readonly WorkoutConfig _c;
    private readonly TokenStore _tokens;
    private readonly ActivityStore _store;
    private readonly ILogger<StravaClient> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public StravaClient(HttpClient http, WorkoutConfig c, TokenStore tokens, ActivityStore store, ILogger<StravaClient> log)
    {
        _http = http; _c = c; _tokens = tokens; _store = store; _log = log;
    }

    public async Task ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _c.StravaClientId,
                ["client_secret"] = _c.StravaClientSecret,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
            });
            using var resp = await _http.PostAsync("https://www.strava.com/oauth/token", form, ct);
            resp.EnsureSuccessStatusCode();
            var tok = await resp.Content.ReadFromJsonAsync<StravaTokenResponse>(JsonOpts, ct)
                ?? throw new InvalidOperationException("empty token response");
            ValidateToken(tok);
            // Do not let activities from a previously linked athlete grant credit.
            await _store.SaveAsync(new ActivitySnap(DateTimeOffset.UtcNow, new()));
            await _tokens.SaveAsync(new StravaToken(tok.AccessToken, tok.RefreshToken, tok.ExpiresAt));
        }
        finally { _gate.Release(); }
    }

    async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        var t = await _tokens.LoadAsync() ?? throw new InvalidOperationException("Strava not linked. Visit /auth/login first.");
        if (t.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds() > 300) return t.AccessToken;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _c.StravaClientId,
            ["client_secret"] = _c.StravaClientSecret,
            ["refresh_token"] = t.RefreshToken,
            ["grant_type"] = "refresh_token",
        });
        using var resp = await _http.PostAsync("https://www.strava.com/oauth/token", form, ct);
        resp.EnsureSuccessStatusCode();
        var tok = await resp.Content.ReadFromJsonAsync<StravaTokenResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("empty refresh response");
        ValidateToken(tok);
        await _tokens.SaveAsync(new StravaToken(tok.AccessToken, tok.RefreshToken, tok.ExpiresAt));
        return tok.AccessToken;
    }

    public async Task<object> SyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await SyncCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<object> SyncCoreAsync(CancellationToken ct)
    {
        var access = await AccessTokenAsync(ct);
        var after = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        var all = new Dictionary<long, CachedActivity>();
        // Replace a complete bounded snapshot. Merging kept deleted workouts
        // forever and a five-page cutoff silently missed activities.
        for (int page = 1; ; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.strava.com/api/v3/athlete/activities?per_page=100&page={page}&after={after}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var acts = await resp.Content.ReadFromJsonAsync<List<StravaActivity>>(JsonOpts, ct)
                ?? throw new JsonException("Empty activity response.");
            foreach (var a in acts)
                all[a.Id] = new CachedActivity(a.Id, a.Name ?? "", a.Type ?? "", a.SportType,
                    DateTimeOffset.Parse(a.StartDate, CultureInfo.InvariantCulture), a.MovingTime, a.Manual);
            if (acts.Count < 100) break;
        }
        var snap = new ActivitySnap(DateTimeOffset.UtcNow, all.Values.OrderByDescending(a => a.StartUtc).ToList());
        await _store.SaveAsync(snap);
        _log.LogInformation("Strava sync: {N} activities cached.", snap.Activities.Count);
        return new { activities = snap.Activities.Count, updatedAtUtc = snap.UpdatedAtUtc };
    }

    static void ValidateToken(StravaTokenResponse token)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken) || string.IsNullOrWhiteSpace(token.RefreshToken)
            || token.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            || token.ExpiresAt > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            throw new JsonException("Invalid Strava token response.");
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
            try { await _strava.SyncAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (InvalidOperationException ex) { _log.LogWarning("sync skipped: {M}", ex.Message); }
            catch (Exception ex) { _log.LogWarning(ex, "strava sync failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
