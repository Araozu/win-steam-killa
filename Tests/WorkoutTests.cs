using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class WorkoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "workout-tests-" + Guid.NewGuid());
    private readonly Dictionary<string, string?> _previous = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeStrava _upstream = new();
    private readonly HttpClient _client;
    private WorkoutConfig Config => _factory.Services.GetRequiredService<WorkoutConfig>();
    private TokenStore Tokens => _factory.Services.GetRequiredService<TokenStore>();
    private ActivityStore Activities => _factory.Services.GetRequiredService<ActivityStore>();

    public WorkoutTests()
    {
        foreach (var pair in new Dictionary<string, string>
        {
            ["DATA_DIR"] = _dir, ["API_TOKEN"] = "test-secret", ["USER_TIMEZONE"] = "America/Santiago",
            ["STRAVA_CLIENT_ID"] = "123", ["STRAVA_CLIENT_SECRET"] = "client-secret",
            ["STRAVA_REDIRECT_URI"] = "https://localhost/auth/callback", ["WORKOUT_MULTIPLIER"] = "4",
            ["MIN_WORKOUT_SECONDS"] = "600", ["MAX_DOTA_SECONDS"] = "14400", ["COUNT_MANUAL"] = "false",
        }) SetEnv(pair.Key, pair.Value);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            var worker = s.Single(x => x.ImplementationType == typeof(SyncWorker));
            s.Remove(worker);
            s.AddHttpClient("strava").ConfigurePrimaryHttpMessageHandler(() => _upstream);
        }));
        _client = _factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
    }
    private void SetEnv(string key, string value)
    {
        if (!_previous.ContainsKey(key)) _previous[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }
    private void Authorize() => _client.DefaultRequestHeaders.Authorization = new("Bearer", "test-secret");
    private async Task SeedToken(bool expired = false) => await Tokens.SaveAsync(new("access", "refresh",
        DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1).ToUnixTimeSeconds()));
    private static CachedActivity Activity(long id, string start, long seconds = 600, bool manual = false) =>
        new(id, "Workout", "Run", null, DateTimeOffset.Parse(start, CultureInfo.InvariantCulture), seconds, manual);
    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var pair in _previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task CreditRequiresBearerAndStrictIsoDates()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/credit")).StatusCode);
        Authorize();
        foreach (var day in new[] { "11-14-2026", "2026-1-1", "garbage", "" })
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/credit?day=" + day)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/credit?day=2026-11-14")).StatusCode);
    }

    [Fact]
    public async Task CreditUsesLocalThreeAmBoundaryAndExcludesManualShortWorkouts()
    {
        await Activities.SaveAsync(new(DateTimeOffset.UtcNow,
        [
            Activity(1, "2026-11-14T05:59:59Z"), // 02:59:59 Santiago: previous logical day
            Activity(2, "2026-11-14T06:00:00Z", 900),
            Activity(3, "2026-11-14T12:00:00Z", 600, manual: true),
            Activity(4, "2026-11-14T12:30:00Z", 599),
        ]));
        Authorize();
        var credit = await _client.GetFromJsonAsync<JsonElement>("/api/credit?day=2026-11-14");
        Assert.Equal(900, credit.GetProperty("workoutSeconds").GetInt64());
        Assert.Equal(3600, credit.GetProperty("dotaSeconds").GetInt64());
        Assert.Equal(1, credit.GetProperty("activityCount").GetInt32());
        var previous = await _client.GetFromJsonAsync<JsonElement>("/api/credit?day=2026-11-13");
        Assert.Equal(600, previous.GetProperty("workoutSeconds").GetInt64());
        var activities = await _client.GetFromJsonAsync<JsonElement>("/api/activities?from=2026-11-13&to=2026-11-13");
        Assert.Equal(1, activities.GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/activities?from=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/activities?from=2026-11-14&to=2026-11-13")).StatusCode);
    }

    [Fact]
    public async Task OAuthCannotStartOrReplaceTokensWithoutOwnerTokenAndBrowserState()
    {
        var login = await _client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Null(login.Headers.Location);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "wrong" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.GetAsync("/auth/callback?code=attacker&scope=activity:read_all")).StatusCode);
        Assert.Null(await Tokens.LoadAsync());
        Assert.Equal(0, _upstream.Calls);
    }

    [Fact]
    public async Task OAuthStateIsSingleUseAndNewAccountClearsOldActivities()
    {
        await Activities.SaveAsync(new(DateTimeOffset.UtcNow, [Activity(9, "2026-11-14T12:00:00Z")]));
        var login = await _client.PostAsync("/auth/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["token"] = "test-secret" }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        var state = query["state"].ToString();
        Assert.Equal(64, state.Length);
        var callback = "/auth/callback?code=valid&scope=activity:read_all&state=" + state;
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(callback)).StatusCode);
        Assert.NotNull(await Tokens.LoadAsync());
        Assert.Empty((await Activities.LoadAsync())!.Activities);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(callback)).StatusCode);
        Assert.Equal(1, _upstream.TokenRequests);
    }

    [Fact]
    public async Task SyncRemovesDeletedActivitiesAndKeepsLastSnapshotOnFailure()
    {
        await SeedToken();
        await Activities.SaveAsync(new(DateTimeOffset.UtcNow, [Activity(1, "2026-11-14T12:00:00Z")]));
        Authorize();
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/sync", null)).StatusCode);
        Assert.Empty((await Activities.LoadAsync())!.Activities);
        await Activities.SaveAsync(new(DateTimeOffset.UtcNow, [Activity(2, "2026-11-14T12:00:00Z")]));
        _upstream.FailActivities = true;
        Assert.Equal(HttpStatusCode.BadGateway, (await _client.PostAsync("/api/sync", null)).StatusCode);
        Assert.Single((await Activities.LoadAsync())!.Activities);
    }

    [Fact]
    public async Task ConcurrentSyncRefreshesRotatingTokenOnlyOnce()
    {
        await SeedToken(expired: true);
        var strava = _factory.Services.GetRequiredService<StravaClient>();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => strava.SyncAsync()));
        Assert.Equal(1, _upstream.TokenRequests);
        Assert.Equal("new-refresh", (await Tokens.LoadAsync())!.RefreshToken);
    }

    [Fact]
    public async Task SyncReadsBeyondFivePages()
    {
        await SeedToken();
        _upstream.FullPages = 5;
        var strava = _factory.Services.GetRequiredService<StravaClient>();
        await strava.SyncAsync();
        Assert.Equal(6, _upstream.ActivityRequests);
        Assert.Equal(500, (await Activities.LoadAsync())!.Activities.Count);
    }

    [Fact]
    public void ConfigurationRejectsInvalidZoneAndUnsafeMultiplier()
    {
        foreach (var multiplier in new[] { "NaN", "Infinity", "-1", "garbage" })
        {
            SetEnv("WORKOUT_MULTIPLIER", multiplier);
            Assert.Throws<InvalidOperationException>(WorkoutConfig.FromEnv);
        }
        SetEnv("WORKOUT_MULTIPLIER", "4");
        SetEnv("USER_TIMEZONE", "typo/zone");
        Assert.Throws<InvalidOperationException>(WorkoutConfig.FromEnv);
    }

    [Fact]
    public void CreditCapsBeforeConvertingLargeValuesToInteger()
    {
        Assert.Equal(14400, WorkoutLogic.DotaCredit(long.MaxValue, Config));
        Assert.Equal(long.MaxValue, WorkoutLogic.DotaCredit(long.MaxValue, Config with { MaxDotaSeconds = 0 }));
    }

    private sealed class FakeStrava : HttpMessageHandler
    {
        public int Calls, TokenRequests, ActivityRequests, FullPages;
        public bool FailActivities;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            await Task.Delay(10, ct); // Let concurrent callers expose token-refresh races.
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token"))
            {
                TokenRequests++;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                {
                    access_token = "new-access", refresh_token = "new-refresh",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                }) };
            }
            ActivityRequests++;
            if (FailActivities) return new(HttpStatusCode.BadGateway);
            int page = int.Parse(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["page"].ToString());
            var entries = page <= FullPages ? Enumerable.Range((page - 1) * 100, 100).Select(i => new
            {
                id = i, name = "run", type = "Run", start_date = "2026-11-14T12:00:00Z", moving_time = 600, manual = false,
            }).ToArray() : [];
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(entries) };
        }
    }
}
