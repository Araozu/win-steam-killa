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
    public async Task LocalCreditUsesCurrentLogicalWeekAndCapsTheResult()
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
                ("previous-week", new DateTime(2026, 11, 9, 2, 59, 0), 600),
                ("week-start", new DateTime(2026, 11, 9, 3, 0, 0), 612),
                ("later-today", new DateTime(2026, 11, 11, 2, 0, 0), 2),
                ("tomorrow", new DateTime(2026, 11, 11, 3, 0, 0), 600),
                ("later-this-week", new DateTime(2026, 11, 12, 7, 0, 0), 10_000)
            })
            {
                var local = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start));
                archive.Sessions[id] = new WorkoutSession(new(id, WorkoutDataBatch.SimpleHealthSource, local, "manual_entry"),
                    local, local.AddSeconds(seconds), seconds, null, null);
            }
            archive.Save(path);

            var source = new LocalWorkoutCreditSource(new WorkoutDataSync(http, "https://workout.example/base/", "secret", path));
            Assert.Equal(2019, await source.GetWeeklyEarnedSecondsAsync("2026-11-09", "2026-11-09", default));
            Assert.Equal(2026, await source.GetWeeklyEarnedSecondsAsync("2026-11-09", "2026-11-10", default));
            Assert.Equal((int)Limits.DotaWeeklyBudget.TotalSeconds,
                await source.GetWeeklyEarnedSecondsAsync("2026-11-09", "2026-11-12", default));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task InvalidFeedFailsSync()
    {
        using var http = new HttpClient(new Handler(_ => Json("{}")));
        var source = new LocalWorkoutCreditSource(new WorkoutDataSync(http, "https://example.com", "secret", "unused.json"));
        await Assert.ThrowsAsync<JsonException>(() => source.GetWeeklyEarnedSecondsAsync("2026-11-09", "2026-11-09", default));
    }

    [Fact]
    public async Task UnavailableWorkoutFeedKeepsCachedWeekCredit()
    {
        string dir = Path.Combine(Path.GetTempPath(), "credit-cache-" + Guid.NewGuid());
        try
        {
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
            var now = new DateTime(2026, 11, 10, 21, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var engine = new TimerEngine(NullLogger<TimerEngine>.Instance, new StateStore(Path.Combine(dir, "state.json")),
                () => now, workout: new LocalWorkoutCreditSource(new WorkoutDataSync(http, "https://example.com", "secret",
                    Path.Combine(dir, "workouts.json"))));
            engine.ApplyWorkoutEarned("2026-11-09", 1800, now);
            Assert.False(await engine.RefreshWorkoutCreditsAsync());
            // Read persisted state without querying real processes.
            Assert.Equal(1800, new StateStore(Path.Combine(dir, "state.json")).Load()!.WorkoutDotaEarnedSeconds);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
