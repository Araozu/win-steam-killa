using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = CaptureConfig.MaxBodyBytes);
var cfg = CaptureConfig.FromEnv();
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<CaptureStore>();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { ok = true, mode = "capture-only", timeUtc = DateTimeOffset.UtcNow }));

bool Authorized(HttpRequest req, CaptureConfig c) => !string.IsNullOrWhiteSpace(c.ApiToken)
    && req.Headers.TryGetValue("Authorization", out var h)
    && h.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
    && CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(h.ToString()[7..])),
        SHA256.HashData(Encoding.UTF8.GetBytes(c.ApiToken)));

// Capture any body format. The real Notify/Tasker payload is deliberately not
// interpreted until hardware samples establish its fields, units and semantics.
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

// Preserve cached timer credit while the replacement pipeline is unimplemented.
// Returning a successful zero would incorrectly erase an existing week's cache.
app.MapGet("/api/credit", (HttpRequest req, CaptureConfig c) => !Authorized(req, c)
    ? Results.Unauthorized()
    : Results.Problem("Workout credit mapping awaits real Notify/Tasker samples. This server only captures data.",
        statusCode: StatusCodes.Status503ServiceUnavailable));

app.Run();
public partial class Program { }
