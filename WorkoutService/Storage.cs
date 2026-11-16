using System.Text;
using System.Text.Json;

sealed record RequestCapture(string Id, DateTimeOffset ReceivedAtUtc, string? ContentType,
    string UserAgent, int BodyLength, string? BodyText, string BodyBase64);

sealed class CaptureStore
{
    private readonly string _directory;
    public CaptureStore(CaptureConfig config) => _directory = Path.Combine(config.DataDir, "captures");

    public async Task<RequestCapture> SaveAsync(byte[] body, string? contentType, string userAgent, CancellationToken ct)
    {
        string? text;
        try { text = new UTF8Encoding(false, true).GetString(body); }
        catch (DecoderFallbackException) { text = null; }
        var capture = new RequestCapture(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            contentType, userAgent, body.Length, text, Convert.ToBase64String(body));
        Directory.CreateDirectory(_directory);
        // IDs and filenames come from the server, never from phone input. Atomic
        // publication means readers only see completed captures. Keep every retry
        // so duplicate delivery and changing payloads can be inspected later.
        var path = Path.Combine(_directory, capture.Id + ".json");
        var temp = path + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await JsonSerializer.SerializeAsync(file, capture, cancellationToken: ct);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return capture;
    }
}
