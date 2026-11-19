using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Service;
using Shared;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class WorkoutCaptureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "capture-tests-" + Guid.NewGuid());
    private readonly string? _oldDir = Environment.GetEnvironmentVariable("DATA_DIR");
    private readonly string? _oldToken = Environment.GetEnvironmentVariable("API_TOKEN");
    private readonly string? _oldCaptureToken = Environment.GetEnvironmentVariable("CAPTURE_API_TOKEN");
    private readonly string? _oldSyncToken = Environment.GetEnvironmentVariable("SYNC_API_TOKEN");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public WorkoutCaptureTests()
    {
        Environment.SetEnvironmentVariable("DATA_DIR", _dir);
        Environment.SetEnvironmentVariable("API_TOKEN", "test-secret");
        Environment.SetEnvironmentVariable("CAPTURE_API_TOKEN", null);
        Environment.SetEnvironmentVariable("SYNC_API_TOKEN", null);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    private void Authorize() => _client.DefaultRequestHeaders.Authorization = new("Bearer", "test-secret");
    // Queued workout uploads.
    private string[] Files() => Saved("captures");
    // Uploads kept only for inspection (no workout data).
    private string[] Diagnostics() => Saved("diagnostics");
    private string[] Saved(string folder) => Directory.Exists(Path.Combine(_dir, folder))
        ? Directory.GetFiles(Path.Combine(_dir, folder), "*.json") : [];

    private const string Workout = """
        {"timestamp":"2026-11-18T14:24:49Z","exercise":[
            {"start_time":"2026-11-18T07:32:32.814-05:00","end_time":"2026-11-18T07:42:44.998-05:00",
             "duration_seconds":612,"type":"0","title":"Training",
             "metadata":{"id":"workout","data_origin":"com.mc.simplehealth","recording_method":"manual_entry",
                         "last_modified_time":"2026-11-18T07:49:23.780-05:00"}}]}
        """;

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        Environment.SetEnvironmentVariable("DATA_DIR", _oldDir);
        Environment.SetEnvironmentVariable("API_TOKEN", _oldToken);
        Environment.SetEnvironmentVariable("CAPTURE_API_TOKEN", _oldCaptureToken);
        Environment.SetEnvironmentVariable("SYNC_API_TOKEN", _oldSyncToken);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task MissingWrongAndUnconfiguredTokensCannotCapture()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/api/capture", new StringContent("test"))).StatusCode);
        _client.DefaultRequestHeaders.Authorization = new("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/api/capture", new StringContent("test"))).StatusCode);
        Environment.SetEnvironmentVariable("API_TOKEN", "");
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/capture", new StringContent("test"))).StatusCode);
        Assert.Empty(Files());
        Assert.Empty(Diagnostics());
    }

    [Theory]
    [InlineData("application/json", "{\"exercise\":[{\"title\":\"caminata 🏃\",\"duration_seconds\":612}]}")]
    [InlineData("application/json", "{not valid JSON")]
    [InlineData("application/x-www-form-urlencoded", "probe=unexpected-format")]
    [InlineData("text/plain", "Workout finished\nDuration: 25 minutes")]
    [InlineData("text/plain", "")]
    public async Task CapturesUnknownFormatsExactlyWithoutLeakingCredentials(string contentType, string raw)
    {
        Authorize();
        _client.DefaultRequestHeaders.Add("Cookie", "private-cookie");
        _client.DefaultRequestHeaders.Add("User-Agent", "HealthConnectWebhook-test");
        var response = await _client.PostAsync("/api/capture?token=private-query", new StringContent(raw, Encoding.UTF8, contentType));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(reply.GetProperty("queued").GetBoolean());
        Assert.Empty(Files());
        var savedText = await File.ReadAllTextAsync(Assert.Single(Diagnostics()));
        var capture = JsonSerializer.Deserialize<RequestCapture>(savedText)!;
        Assert.Equal(raw, capture.BodyText);
        Assert.Equal(Encoding.UTF8.GetBytes(raw), Convert.FromBase64String(capture.BodyBase64));
        Assert.Equal(Encoding.UTF8.GetByteCount(raw), capture.BodyLength);
        Assert.StartsWith(contentType, capture.ContentType);
        Assert.Equal("HealthConnectWebhook-test", capture.UserAgent);
        Assert.Equal(reply.GetProperty("id").GetString(), capture.Id);
        foreach (var secret in new[] { "test-secret", "private-cookie", "private-query" })
            Assert.DoesNotContain(secret, savedText);
    }

    [Fact]
    public async Task BinaryPayloadIsRetainedAsBase64()
    {
        Authorize();
        byte[] raw = [0xff, 0x00, 0x80];
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/capture", new ByteArrayContent(raw))).StatusCode);
        var capture = JsonSerializer.Deserialize<RequestCapture>(await File.ReadAllTextAsync(Assert.Single(Diagnostics())))!;
        Assert.Null(capture.BodyText);
        Assert.Equal(raw, Convert.FromBase64String(capture.BodyBase64));
    }

    [Fact]
    public async Task ConcurrentRetriesAreSeparateCapturesAndSurviveNewStoreInstance()
    {
        Authorize();
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            _client.PostAsync("/api/capture", new StringContent(Workout, Encoding.UTF8, "application/json"))));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(20, Files().Length);
        var store = new CaptureStore(new("test-secret", _dir));
        await store.SaveAsync(Encoding.UTF8.GetBytes(Workout), "application/json", "test", CancellationToken.None);
        Assert.Equal(21, Files().Length);
        Assert.All(Files(), path => Assert.NotNull(JsonSerializer.Deserialize<RequestCapture>(File.ReadAllText(path))!.WorkoutData));
    }

    [Fact]
    public async Task UploadsWithoutWorkoutDataStayOutOfTheQueue()
    {
        Authorize();
        await Upload("""{"timestamp":"2026-11-18T14:38:17Z","test":true}""");
        await Upload("""{"timestamp":"2026-11-18T14:38:17Z","exercise":[]}""");
        Assert.Empty(Files());
        Assert.Equal(2, Diagnostics().Length);

        // A capture queued before normalization existed, holding no workout,
        // is moved out of the queue the first time it is read.
        var legacy = JsonSerializer.Deserialize<RequestCapture>(File.ReadAllText(Diagnostics()[0]))!;
        Directory.CreateDirectory(Path.Combine(_dir, "captures"));
        File.Move(Diagnostics()[0], Path.Combine(_dir, "captures", legacy.Id + ".json"));
        Assert.Empty(await _client.GetFromJsonAsync<WorkoutDelivery[]>("/api/workouts") ?? throw new InvalidDataException());
        Assert.Empty(Files());
        Assert.Equal(2, Diagnostics().Length);
    }

    [Fact]
    public void FractionalDurationsAreFlooredAndForeignRowsAreCounted()
    {
        var batch = HealthConnectPayload.Read(Workout.Replace("\"duration_seconds\":612", "\"duration_seconds\":612.184")
            .Replace("\"exercise\":[", """
                "exercise":[{"start_time":"2026-11-18T07:00:00-05:00","end_time":"2026-11-18T07:10:00-05:00","duration_seconds":600,
                  "metadata":{"id":"other","data_origin":"com.example.other","last_modified_time":"2026-11-18T07:10:00-05:00"}},
                """), out int skipped);
        Assert.Equal(612, Assert.Single(batch!.Sessions).DurationSeconds);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public async Task OversizedKnownAndUnknownLengthBodiesAreRejectedWithoutSaving()
    {
        Authorize();
        using var known = new ByteArrayContent(new byte[CaptureConfig.MaxBodyBytes + 1]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _client.PostAsync("/api/capture", known)).StatusCode);
        using var unknown = new UnknownLengthContent(new byte[CaptureConfig.MaxBodyBytes + 1]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _client.PostAsync("/api/capture", unknown)).StatusCode);
        Assert.Empty(Files());
        Assert.Empty(Diagnostics());
    }

    [Fact]
    public async Task HealthIsPublicAndEveryApiRouteChecksTheTokenBeforeReadingTheBody()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health")).StatusCode);
        var malformed = await _client.PostAsync("/api/workouts/ack", new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);
        Assert.Contains("Bearer", malformed.Headers.WwwAuthenticate.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/credit")).StatusCode);
        Authorize();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsync("/api/workouts/ack", new StringContent("{not json", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/credit")).StatusCode); // Retired endpoint.
    }

    [Fact]
    public async Task ScopedTokensOnlyAuthorizeTheirOwnEndpoints()
    {
        Environment.SetEnvironmentVariable("CAPTURE_API_TOKEN", "capture-secret");
        Environment.SetEnvironmentVariable("SYNC_API_TOKEN", "sync-secret");
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new("Bearer", "capture-secret");
        var upload = await client.PostAsync("/api/capture/", new StringContent(Workout));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/workouts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/workouts/ack", new WorkoutAcknowledgement([]))).StatusCode);

        client.DefaultRequestHeaders.Authorization = new("Bearer", "test-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/workouts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync("/api/capture", new StringContent(Workout))).StatusCode);

        client.DefaultRequestHeaders.Authorization = new("Bearer", "sync-secret");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync("/api/capture/", new StringContent(Workout))).StatusCode);
        var batch = Assert.Single((await client.GetFromJsonAsync<WorkoutDelivery[]>("/api/workouts"))!);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/workouts/ack", new WorkoutAcknowledgement([batch.Id]))).StatusCode);
    }

    [Fact]
    public async Task UnreadableCaptureMovesToDiagnosticsAndLaterWorkoutsAreDelivered()
    {
        Authorize();
        await Upload(Workout);
        var valid = Assert.Single(Files());
        var corrupt = Path.Combine(_dir, "captures", Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(corrupt, "{");
        File.SetLastWriteTimeUtc(corrupt, File.GetLastWriteTimeUtc(valid).AddMinutes(-1));

        var batch = Assert.Single((await _client.GetFromJsonAsync<WorkoutDelivery[]>("/api/workouts"))!);
        Assert.Equal(Path.GetFileNameWithoutExtension(valid), batch.Id);
        Assert.Equal(valid, Assert.Single(Files()));
        Assert.Equal("{", await File.ReadAllTextAsync(Assert.Single(Diagnostics())));
    }

    [Fact]
    public async Task SyncPersistsBeforeAckAndLinksUploadsAcrossRunsWithoutDuplicatingWorkouts()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/workouts")).StatusCode);
        Authorize();
        await Upload("""
            {"timestamp":"2026-11-18T14:24:49.123456789Z","exercise":[
                {"start_time":"2026-11-18T07:32:32.814-05:00","end_time":"2026-11-18T07:42:44.998-05:00",
                 "duration_seconds":612,"type":"0","title":"Training",
                 "metadata":{"id":"workout","data_origin":"com.mc.simplehealth","recording_method":"manual_entry",
                             "last_modified_time":"2026-11-18T07:49:23.780-05:00"}}]}
            """);
        var prior = JsonSerializer.Deserialize<RequestCapture>(await File.ReadAllTextAsync(Assert.Single(Files())))!;
        // Existing capture files won't have a normalized batch until first read.
        await File.WriteAllTextAsync(Assert.Single(Files()), JsonSerializer.Serialize(prior with { WorkoutData = null }));
        var archivePath = Path.Combine(_dir, "local", "workouts.json");
        Directory.CreateDirectory(archivePath + ".tmp");
        var sync = new WorkoutDataSync(_client, _client.BaseAddress!.AbsoluteUri, "test-secret", archivePath,
            clock: WorkoutClientTests.Clock);
        var failure = await Record.ExceptionAsync(() => sync.SyncAsync(default));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Single(Files());
        Directory.Delete(archivePath + ".tmp");
        await sync.SyncAsync(default);
        Assert.Empty(Files());
        Assert.Single(WorkoutArchive.Load(archivePath).Sessions);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.PostAsJsonAsync("/api/workouts/ack", new WorkoutAcknowledgement([prior.Id]))).StatusCode);

        await Upload(prior.BodyText!);
        await Upload("""
            {"timestamp":"2026-11-18T14:38:17Z",
             "heart_rate":[
                {"time":"2026-11-18T07:31:00-05:00","avg":90,
                 "metadata":{"id":"hr","data_origin":"com.mc.simplehealth","last_modified_time":"2026-11-18T14:35:00Z"}},
                {"time":"2026-11-18T07:32:00-05:00","avg":114,"min":113,"max":115,
                 "metadata":{"id":"hr","data_origin":"com.mc.simplehealth","last_modified_time":"2026-11-18T14:35:00Z"}},
                {"time":"2026-11-18T07:33:00-05:00","avg":120,
                 "metadata":{"id":"hr","data_origin":"com.mc.simplehealth","last_modified_time":"2026-11-18T14:35:00Z"}}],
             "total_calories":[
                {"start_time":"2026-11-18T07:32:32.814-05:00","end_time":"2026-11-18T07:42:44.998-05:00","calories":0.071,
                 "metadata":{"id":"calories","data_origin":"com.mc.simplehealth","last_modified_time":"2026-11-18T14:35:00Z"}}]}
            """);
        await sync.SyncAsync(default);

        var archive = WorkoutArchive.Load(archivePath);
        Assert.Equal(612, Assert.Single(archive.Sessions).Value.DurationSeconds);
        Assert.Equal(3, archive.HeartRate.Count);
        var links = archive.SensorLinks["workout"];
        Assert.Equal(new double[] { 114, 120 }, links.HeartRateKeys.Select(key => archive.HeartRate[key].Reading.Bpm));
        Assert.Equal("calories", Assert.Single(links.TotalCalorieIds));
        Assert.Empty(Files());

        var workout = Assert.Single(archive.Sessions).Value;
        string day = LogicalDay.ForUtc(workout.Start.UtcDateTime);
        Assert.True(LogicalDay.TryParse(day, out var date));
        var now = PolicyClock.ToUtc(date.AddHours(21));
        var state = new StateStore(Path.Combine(_dir, "timer.json"));
        var engine = new TimerEngine(Microsoft.Extensions.Logging.Abstractions.NullLogger<TimerEngine>.Instance,
            state, () => now, workout: new LocalWorkoutCreditSource(sync));
        Assert.True(await engine.RefreshWorkoutCreditsAsync());
        Assert.Equal(2019, state.Load()!.DotaWeekdayEarnedSeconds);

        await Upload("""{"timestamp":"2026-11-18T14:40:00Z","deleted":[{"type":"exercise","id":"workout"}]}""");
        await Upload(prior.BodyText!);
        Assert.True(await engine.RefreshWorkoutCreditsAsync());
        Assert.Empty(WorkoutArchive.Load(archivePath).Sessions);
        Assert.Equal(0, state.Load()!.DotaWeekdayEarnedSeconds);
    }

    [Fact]
    public async Task CleanupRemovesAcknowledgedAndMonthOldCaptures()
    {
        Authorize();
        var old = await Upload(Workout);
        var acknowledged = await Upload(Workout);
        var recent = await Upload(Workout);
        var oldDiagnostic = await Upload("""{"timestamp":"2026-11-18T14:38:17Z","test":true}""");
        var recentDiagnostic = await Upload("{}");
        var now = DateTimeOffset.UtcNow;
        var expired = now.UtcDateTime.AddMonths(-1).AddMinutes(-1);
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "captures", old + ".json"), expired);
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "diagnostics", oldDiagnostic + ".json"), expired);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync("/api/workouts/ack",
            new WorkoutAcknowledgement([acknowledged]))).StatusCode);

        new CaptureStore(new("test-secret", _dir)).Cleanup(now);

        Assert.Equal(recent + ".json", Path.GetFileName(Assert.Single(Files())));
        Assert.Equal(recentDiagnostic + ".json", Path.GetFileName(Assert.Single(Diagnostics())));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "acknowledged")));
        Assert.Equal(recent, Assert.Single(await _client.GetFromJsonAsync<WorkoutDelivery[]>("/api/workouts")
            ?? throw new InvalidDataException()).Id);
    }

    private async Task<string> Upload(string body)
    {
        using var response = await _client.PostAsync("/api/capture", new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return reply.RootElement.GetProperty("id").GetString()!;
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}
