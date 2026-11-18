using System.Security.Cryptography;
using System.Text;
using Shared;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = CaptureConfig.MaxBodyBytes);
var cfg = CaptureConfig.FromEnv();
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<CaptureStore>();
builder.Services.AddHostedService<CaptureCleanup>();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { ok = true, mode = "workout-sync", timeUtc = DateTimeOffset.UtcNow }));

bool Authorized(HttpRequest req, CaptureConfig c) => !string.IsNullOrWhiteSpace(c.ApiToken)
    && req.Headers.TryGetValue("Authorization", out var h)
    && h.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
    && CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(h.ToString()[7..])),
        SHA256.HashData(Encoding.UTF8.GetBytes(c.ApiToken)));

// Preserve the original webhook body for inspecting Health Connect samples.
app.MapPost("/api/capture", async (HttpRequest req, CaptureConfig c, CaptureStore store) =>
{
    if (!Authorized(req, c)) return Results.Unauthorized();
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
    return Results.Ok(new { captured = true, id = capture.Id, receivedAtUtc = capture.ReceivedAtUtc, creditGranted = false });
});

app.MapGet("/api/workouts", async (HttpRequest req, CaptureConfig c, CaptureStore store) =>
{
    if (!Authorized(req, c)) return Results.Unauthorized();
    req.HttpContext.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await store.GetPendingAsync(req.HttpContext.RequestAborted));
});

app.MapPost("/api/workouts/ack", (HttpRequest req, CaptureConfig c, CaptureStore store, WorkoutAcknowledgement ack) =>
{
    if (!Authorized(req, c)) return Results.Unauthorized();
    if (ack.Ids is null || ack.Ids.Length is < 1 or > WorkoutDataBatch.PageSize
        || ack.Ids.Any(id => !Guid.TryParseExact(id, "N", out _)))
        return Results.BadRequest();
    store.Acknowledge(ack.Ids);
    return Results.NoContent();
});

// Earlier clients retain cached credit on HTTP 503.
app.MapGet("/api/credit", (HttpRequest req, CaptureConfig c) => !Authorized(req, c)
    ? Results.Unauthorized()
    : Results.Problem("Health Connect captures are available for inspection. Workout credit is unavailable.",
        statusCode: StatusCodes.Status503ServiceUnavailable));

app.Run();
public partial class Program { }
