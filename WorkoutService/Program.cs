using System.Security.Cryptography;
using System.Text;
using Shared;

// Container health probe: `dotnet WorkoutService.dll --healthcheck` exits 0 when
// the running receiver answers /health. The runtime image needs no curl.
if (args.Contains("--healthcheck"))
{
    string url = Environment.GetEnvironmentVariable("HEALTHCHECK_URL") ?? "http://127.0.0.1:8080/health";
    try
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var reply = await probe.GetAsync(url);
        return reply.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.Error.WriteLine($"Health check failed: {ex.Message}");
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = CaptureConfig.MaxBodyBytes);
var cfg = CaptureConfig.FromEnv();
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<CaptureStore>();
builder.Services.AddHostedService<CaptureCleanup>();

var app = builder.Build();
app.UseRouting();

// Every /api endpoint needs the bearer token. This runs before endpoint
// parameter binding, so unauthenticated bodies are never read or parsed.
app.Use(async (context, next) =>
{
    var endpoint = context.GetEndpoint();
    var access = endpoint is null
        ? WorkoutAccess.Sync
        : endpoint.Metadata.GetMetadata<WorkoutAccessScope>()?.Access;
    if (context.Request.Path.StartsWithSegments("/api")
        && (access is null || !Authorized(context.Request, cfg.TokenFor(access.Value))))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return;
    }
    await next(context);
});

app.MapGet("/health", () => Results.Ok(new { ok = true, mode = "workout-sync", timeUtc = DateTimeOffset.UtcNow }));

// Preserve the original webhook body for inspecting Health Connect samples.
app.MapPost("/api/capture", async (HttpRequest req, CaptureStore store) =>
{
    if (req.ContentLength > CaptureConfig.MaxBodyBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    using var body = new MemoryStream();
    var buffer = new byte[8192];
    while (true)
    {
        int read = await req.Body.ReadAsync(buffer.AsMemory(0,
            Math.Min(buffer.Length, CaptureConfig.MaxBodyBytes + 1 - (int)body.Length)), req.HttpContext.RequestAborted);
        if (read == 0) break;
        body.Write(buffer, 0, read);
        if (body.Length > CaptureConfig.MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }
    var capture = await store.SaveAsync(body.ToArray(), req.ContentType, req.Headers.UserAgent.ToString(),
        req.HttpContext.RequestAborted);
    return Results.Ok(new { captured = true, id = capture.Id, receivedAtUtc = capture.ReceivedAtUtc, queued = capture.WorkoutData is not null });
}).WithMetadata(new WorkoutAccessScope(WorkoutAccess.Capture));

app.MapGet("/api/workouts", async (HttpContext context, CaptureStore store) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await store.GetPendingAsync(context.RequestAborted));
}).WithMetadata(new WorkoutAccessScope(WorkoutAccess.Sync));

app.MapPost("/api/workouts/ack", (CaptureStore store, WorkoutAcknowledgement ack) =>
{
    if (ack.Ids is null || ack.Ids.Length is < 1 or > WorkoutDataBatch.PageSize
        || ack.Ids.Any(id => !Guid.TryParseExact(id, "N", out _)))
        return Results.BadRequest();
    store.Acknowledge(ack.Ids);
    return Results.NoContent();
}).WithMetadata(new WorkoutAccessScope(WorkoutAccess.Sync));

app.Run();
return 0;

static bool Authorized(HttpRequest req, string token) => !string.IsNullOrWhiteSpace(token)
    && req.Headers.TryGetValue("Authorization", out var h)
    && h.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
    && CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(h.ToString()[7..])),
        SHA256.HashData(Encoding.UTF8.GetBytes(token)));

sealed record WorkoutAccessScope(WorkoutAccess Access);
public partial class Program { }
