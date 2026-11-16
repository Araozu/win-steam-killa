using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class WorkoutCaptureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "capture-tests-" + Guid.NewGuid());
    private readonly string? _oldDir = Environment.GetEnvironmentVariable("DATA_DIR");
    private readonly string? _oldToken = Environment.GetEnvironmentVariable("API_TOKEN");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public WorkoutCaptureTests()
    {
        Environment.SetEnvironmentVariable("DATA_DIR", _dir);
        Environment.SetEnvironmentVariable("API_TOKEN", "test-secret");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    private void Authorize() => _client.DefaultRequestHeaders.Authorization = new("Bearer", "test-secret");
    private string[] Files() => Directory.Exists(Path.Combine(_dir, "captures"))
        ? Directory.GetFiles(Path.Combine(_dir, "captures"), "*.json") : [];

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        Environment.SetEnvironmentVariable("DATA_DIR", _oldDir);
        Environment.SetEnvironmentVariable("API_TOKEN", _oldToken);
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
    }

    [Theory]
    [InlineData("application/json", "{\"unknownField\":\"caminata 🏃\",\"duration\":123}")]
    [InlineData("application/json", "{not valid JSON")]
    [InlineData("application/x-www-form-urlencoded", "event=workoutFinished&duration=25%3A00")]
    [InlineData("text/plain", "Workout finished\nDuration: 25 minutes")]
    [InlineData("text/plain", "")]
    public async Task CapturesUnknownFormatsExactlyWithoutLeakingCredentials(string contentType, string raw)
    {
        Authorize();
        _client.DefaultRequestHeaders.Add("Cookie", "private-cookie");
        _client.DefaultRequestHeaders.Add("User-Agent", "Tasker-test");
        var response = await _client.PostAsync("/api/capture?token=private-query", new StringContent(raw, Encoding.UTF8, contentType));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(reply.GetProperty("creditGranted").GetBoolean());
        var savedText = await File.ReadAllTextAsync(Assert.Single(Files()));
        var capture = JsonSerializer.Deserialize<RequestCapture>(savedText)!;
        Assert.Equal(raw, capture.BodyText);
        Assert.Equal(Encoding.UTF8.GetBytes(raw), Convert.FromBase64String(capture.BodyBase64));
        Assert.Equal(Encoding.UTF8.GetByteCount(raw), capture.BodyLength);
        Assert.StartsWith(contentType, capture.ContentType);
        Assert.Equal("Tasker-test", capture.UserAgent);
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
        var capture = JsonSerializer.Deserialize<RequestCapture>(await File.ReadAllTextAsync(Assert.Single(Files())))!;
        Assert.Null(capture.BodyText);
        Assert.Equal(raw, Convert.FromBase64String(capture.BodyBase64));
    }

    [Fact]
    public async Task ConcurrentRetriesAreSeparateCapturesAndSurviveNewStoreInstance()
    {
        Authorize();
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            _client.PostAsync("/api/capture", new StringContent("same workout"))));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(20, Files().Length);
        var store = new CaptureStore(new("test-secret", _dir));
        await store.SaveAsync(Encoding.UTF8.GetBytes("another"), "text/plain", "test", CancellationToken.None);
        Assert.Equal(21, Files().Length);
        Assert.All(Files(), path => Assert.NotNull(JsonSerializer.Deserialize<RequestCapture>(File.ReadAllText(path))));
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
    }

    [Fact]
    public async Task CaptureModeDoesNotGrantCreditAndStravaRoutesAreGone()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/credit")).StatusCode);
        Authorize();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.GetAsync("/api/credit?day=2026-11-14")).StatusCode);
        foreach (var path in new[] { "/auth/login", "/auth/status", "/auth/callback" })
            Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/sync", null)).StatusCode);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}
