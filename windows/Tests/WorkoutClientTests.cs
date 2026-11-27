using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Service;
using Shared;

public sealed class WorkoutClientTests
{
    [Theory]
    [InlineData("", "", "https://capture.example", "token")]
    [InlineData(" ", " ", "https://capture.example", "token")]
    [InlineData("https://configured.example", "configured-token", "https://configured.example", "configured-token")]
    public void ConfigurationUsesAliasesWhenStructuredSettingsAreBlank(string url, string token, string expectedUrl, string expectedToken)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Workout:BaseUrl"] = url, ["Workout:ApiToken"] = token,
            ["WORKOUT_BASE_URL"] = "https://capture.example", ["WORKOUT_API_TOKEN"] = "token",
        }).Build();
        var settings = WorkoutConnectionSettings.FromConfiguration(config);
        Assert.True(settings.IsConfigured);
        Assert.Equal(expectedUrl, settings.BaseUrl);
        Assert.Equal(expectedToken, settings.ApiToken);
    }

    [Fact]
    public async Task SavedConnectionOverridesFallbackAndRequiresANewTokenForANewUrl()
    {
        string dir = Path.Combine(Path.GetTempPath(), "workout-settings-" + Guid.NewGuid());
        try
        {
            string path = Path.Combine(dir, "connection.json");
            var fallback = new WorkoutConnectionSettings("https://fallback.example", "fallback-token");
            var settings = new WorkoutConnectionStore(path, fallback);
            Assert.Equal(fallback, settings.Current);
            Assert.NotNull(settings.Save("http://workout.example", "token"));
            Assert.NotNull(settings.Save("https://workout.example", ""));
            Assert.NotNull(settings.Save("https://workout.example", "Bearer token"));
            Assert.Null(settings.Save("https://workout.example/base/", "saved-token"));
            Assert.Null(settings.Save("https://workout.example/base", ""));

            var reloaded = new WorkoutConnectionStore(path, fallback);
            Assert.Equal("https://workout.example/base", reloaded.Current.BaseUrl);
            using var http = new HttpClient(new Handler(request =>
            {
                Assert.Equal("https://workout.example/base/api/workouts", request.RequestUri!.AbsoluteUri);
                Assert.Equal("saved-token", request.Headers.Authorization?.Parameter);
                return Json("[]");
            }));
            var source = new ConfiguredWorkoutCreditSource(reloaded, http, Path.Combine(dir, "workouts.json"),
                NullLogger<WorkoutDataSync>.Instance);
            Assert.Equal(default, await source.GetEarnedAsync("2026-11-09", "2026-11-09", default));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task LocalCreditUsesCurrentLogicalWeekWithNoEightHourCap()
    {
        string dir = Path.Combine(Path.GetTempPath(), "credit-cache-" + Guid.NewGuid());
        try
        {
            string path = Path.Combine(dir, "workouts.json");
            using var http = new HttpClient(new Handler(request =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("secret", request.Headers.Authorization?.Parameter);
                Assert.Equal("/base/api/workouts", request.RequestUri!.AbsolutePath);
                return Json("[]");
            }));
            var archive = new WorkoutArchive();
            foreach (var (id, start, seconds) in new[]
            {
                ("previous-week", new DateTime(2026, 11, 8, 23, 50, 0), 600),
                ("week-start", new DateTime(2026, 11, 9, 0, 0, 0), 612),
                ("later-today", new DateTime(2026, 11, 10, 23, 0, 0), 2),
                ("tomorrow", new DateTime(2026, 11, 11, 0, 0, 0), 600),
                ("later-this-week", new DateTime(2026, 11, 12, 7, 0, 0), 10_000)
            })
                archive.Sessions[id] = Session(id, start, seconds);
            archive.Save(path);

            var source = new LocalWorkoutCreditSource(new WorkoutDataSync(http, "https://workout.example/base/", "secret", path,
                clock: Clock));
            Assert.Equal(new(2019, 0), await source.GetEarnedAsync("2026-11-09", "2026-11-09", default));
            Assert.Equal(new(2026, 0), await source.GetEarnedAsync("2026-11-09", "2026-11-10", default));
            Assert.Equal(new(37006, 0), await source.GetEarnedAsync("2026-11-09", "2026-11-12", default));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void OverlappingSessionsCountTheirSharedTimeOnce()
    {
        var start = new DateTime(2026, 11, 10, 7, 0, 0);
        var sessions = new[]
        {
            Session("first", start, 600),
            Session("duplicate", start, 600),              // Same workout saved twice.
            Session("overlap", start.AddMinutes(5), 600),  // Runs 5 minutes past the first.
            Session("separate", start.AddHours(2), 100),
        };
        // (600 + 300) + 100 workout seconds at 3.3x.
        var archive = new WorkoutArchive { Sessions = sessions.ToDictionary(row => row.Info.Id) };
        Assert.Equal(new(3300, 0), WorkoutCredit.ForWeek(archive, "2026-11-09", "2026-11-10"));
        // A nested record never earns more than the sessions report.
        archive.Sessions.Clear();
        archive.Sessions["outer"] = Session("outer", start, 600);
        archive.Sessions["inner"] = Session("inner", start.AddMinutes(1), 60);
        Assert.Equal(new(1980, 0), WorkoutCredit.ForWeek(archive, "2026-11-09", "2026-11-10"));
    }

    [Fact]
    public async Task LateWorkoutsEarnWithinTheSameWeekAndExpireOnMonday()
    {
        string dir = Path.Combine(Path.GetTempPath(), "workout-cutoff-" + Guid.NewGuid());
        try
        {
            var friday = Session("friday", new(2026, 11, 13, 23, 55, 0), 600);
            var saturday = Session("saturday", new(2026, 11, 14, 0, 0, 0), 1200);
            var lateSunday = Session("sunday", new(2026, 11, 15, 12, 0, 0), 600);
            string path = Path.Combine(dir, "workouts.json");
            WorkoutSession[] delivered = [friday, saturday];
            using var http = new HttpClient(new Handler(request =>
            {
                if (request.Method == HttpMethod.Post) return new(HttpStatusCode.NoContent);
                var batch = new WorkoutDataBatch(Clock.GetUtcNow(), delivered, [], [], []);
                return Json(JsonSerializer.Serialize(new[] { new WorkoutDelivery(Guid.NewGuid().ToString("N"), batch) },
                    WorkoutDataSync.Json));
            }));
            var source = new LocalWorkoutCreditSource(new WorkoutDataSync(http, "https://example.com", "secret", path, clock: Clock));
            // Friday's first sync happens on Saturday. Five minutes of overlap
            // earn credit once, in the weekday budget.
            Assert.Equal(new(1980, 2970), await source.GetEarnedAsync("2026-11-09", "2026-11-14", default));
            delivered = [lateSunday];
            Assert.Equal(default, await source.GetEarnedAsync("2026-11-16", "2026-11-16", default));
            Assert.Contains("sunday", WorkoutArchive.Load(path).Sessions.Keys);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ArchiveRecoversItsBackupAndSetsAsideUnreadableFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "archive-" + Guid.NewGuid());
        try
        {
            string path = Path.Combine(dir, "workouts.json");
            var archive = new WorkoutArchive();
            archive.Sessions["a"] = Session("a", new DateTime(2026, 11, 10, 7, 0, 0), 60);
            archive.Save(path);
            archive.Sessions["b"] = Session("b", new DateTime(2026, 11, 10, 8, 0, 0), 60);
            archive.Save(path);
            File.WriteAllText(path, "{ interrupted");
            Assert.Single(WorkoutArchive.Load(path).Sessions);

            File.WriteAllText(path + ".bak", "{ also broken");
            Assert.Empty(WorkoutArchive.Load(path).Sessions);
            Assert.False(File.Exists(path));
            Assert.Equal(2, Directory.GetFiles(dir, "workouts.json*.corrupt-*").Length);
            Assert.Empty(WorkoutArchive.Load(path).Sessions); // Later syncs start cleanly.
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task SyncPrunesRecordsOlderThanTheRetentionPeriod()
    {
        string dir = Path.Combine(Path.GetTempPath(), "archive-" + Guid.NewGuid());
        try
        {
            string path = Path.Combine(dir, "workouts.json");
            var now = Clock.GetUtcNow();
            var archive = new WorkoutArchive();
            archive.Sessions["old"] = Session("old", PolicyClock.FromUtc(now.UtcDateTime).AddDays(-100), 60);
            archive.Sessions["recent"] = Session("recent", PolicyClock.FromUtc(now.UtcDateTime).AddDays(-2), 60);
            archive.Deletions["exercise:gone"] = now.AddDays(-100);
            archive.Save(path);
            using var http = new HttpClient(new Handler(_ => Json("[]")));
            var synced = await new WorkoutDataSync(http, "https://example.com", "secret", path, clock: Clock).SyncAsync(default);
            Assert.Equal(new[] { "recent" }, synced.Sessions.Keys);
            Assert.Empty(synced.Deletions);
            Assert.Equal(new[] { "recent" }, WorkoutArchive.Load(path).Sessions.Keys);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task InvalidFeedFailsSync()
    {
        using var http = new HttpClient(new Handler(_ => Json("{}")));
        var source = new LocalWorkoutCreditSource(new WorkoutDataSync(http, "https://example.com", "secret", "unused.json"));
        await Assert.ThrowsAsync<JsonException>(() => source.GetEarnedAsync("2026-11-09", "2026-11-09", default));
    }

    [Fact]
    public async Task UnavailableWorkoutFeedKeepsCachedWeekCredit()
    {
        string dir = Path.Combine(Path.GetTempPath(), "credit-cache-" + Guid.NewGuid());
        try
        {
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
            var now = PolicyClock.ToUtc(new DateTime(2026, 11, 10, 21, 0, 0));
            var engine = new TimerEngine(NullLogger<TimerEngine>.Instance, new StateStore(Path.Combine(dir, "state.json")),
                () => now, workout: new LocalWorkoutCreditSource(new WorkoutDataSync(http, "https://example.com", "secret",
                    Path.Combine(dir, "workouts.json"))));
            engine.ApplyWorkoutEarned("2026-11-09", new(1800, 0), now);
            Assert.False(await engine.RefreshWorkoutCreditsAsync());
            // Read persisted state without querying real processes.
            Assert.Equal(1800, new StateStore(Path.Combine(dir, "state.json")).Load()!.DotaWeekdayEarnedSeconds);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // Fixed "now" so the 90-day archive retention never expires test data.
    internal static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    // start is a policy-clock (UTC-05) time.
    private static WorkoutSession Session(string id, DateTime start, int seconds)
    {
        var at = new DateTimeOffset(start, PolicyClock.Offset);
        return new WorkoutSession(new(id, WorkoutDataBatch.SimpleHealthSource, at, "manual_entry"),
            at, at.AddSeconds(seconds), seconds, null, null);
    }

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
