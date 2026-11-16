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
    public async Task ClientSumsCurrentWeekWithBearerAndCapsLargeValues()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("secret", request.Headers.Authorization?.Parameter);
            Assert.Equal("/base/api/credit", request.RequestUri!.AbsolutePath);
            string day = request.RequestUri.Query[5..];
            Assert.Equal(calls++ == 0 ? "2026-11-09" : "2026-11-10", day);
            return Json($"{{\"day\":\"{day}\",\"dotaSeconds\":1200}}");
        }));
        var source = new HttpWorkoutCreditSource(http, "https://workout.example/base/", "secret");
        Assert.Equal(2400, await source.GetWeeklyEarnedSecondsAsync("2026-11-09", "2026-11-10", CancellationToken.None));
        Assert.Equal(2, calls);
        using var large = new HttpClient(new Handler(_ => Json("{\"day\":\"2026-11-09\",\"dotaSeconds\":9223372036854775807}")));
        Assert.Equal((int)Limits.DotaWeeklyBudget.TotalSeconds, await new HttpWorkoutCreditSource(large, "https://example.com", "secret")
            .GetWeeklyEarnedSecondsAsync("2026-11-09", "2026-11-10", CancellationToken.None));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"day\":\"2026-11-10\",\"dotaSeconds\":600}")]
    [InlineData("{\"day\":\"2026-11-09\",\"dotaSeconds\":-1}")]
    [InlineData("{\"day\":\"2026-11-09\",\"dotaSeconds\":\"600\"}")]
    public async Task InvalidCreditIsAFailedSyncRatherThanSuccessfulZero(string body)
    {
        using var http = new HttpClient(new Handler(_ => Json(body)));
        var source = new HttpWorkoutCreditSource(http, "https://example.com", "secret");
        await Assert.ThrowsAsync<JsonException>(() => source.GetWeeklyEarnedSecondsAsync("2026-11-09", "2026-11-10", CancellationToken.None));
    }

    [Fact]
    public async Task CaptureOnlyServerResponseKeepsCachedWeekCredit()
    {
        string dir = Path.Combine(Path.GetTempPath(), "credit-cache-" + Guid.NewGuid());
        try
        {
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
            var now = new DateTime(2026, 11, 10, 21, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var engine = new TimerEngine(NullLogger<TimerEngine>.Instance, new StateStore(Path.Combine(dir, "state.json")),
                () => now, workout: new HttpWorkoutCreditSource(http, "https://example.com", "secret"));
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
