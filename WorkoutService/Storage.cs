using System.Text;
using System.Text.Json;
using Shared;

sealed record RequestCapture(string Id, DateTimeOffset ReceivedAtUtc, string? ContentType,
    string UserAgent, int BodyLength, string? BodyText, string BodyBase64)
{
    public WorkoutDataBatch? WorkoutData { get; init; }
}

sealed class CaptureStore
{
    private readonly string _directory;
    private readonly string _acknowledged;
    public CaptureStore(CaptureConfig config)
    {
        _directory = Path.Combine(config.DataDir, "captures");
        _acknowledged = Path.Combine(config.DataDir, "acknowledged");
    }

    public async Task<RequestCapture> SaveAsync(byte[] body, string? contentType, string userAgent, CancellationToken ct)
    {
        string? text;
        try { text = new UTF8Encoding(false, true).GetString(body); }
        catch (DecoderFallbackException) { text = null; }
        var capture = new RequestCapture(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            contentType, userAgent, body.Length, text, Convert.ToBase64String(body))
        {
            WorkoutData = HealthConnectPayload.Read(text)
        };
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

    public async Task<List<WorkoutDelivery>> GetPendingAsync(CancellationToken ct)
    {
        var result = new List<WorkoutDelivery>();
        foreach (var path in Files(_directory, "*.json").OrderBy(File.GetLastWriteTimeUtc))
        {
            ct.ThrowIfCancellationRequested();
            if (Expired(path, DateTimeOffset.UtcNow)) continue;
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var capture = await JsonSerializer.DeserializeAsync<RequestCapture>(file, cancellationToken: ct);
                // Captures made before the sync API was added still participate.
                var batch = capture?.WorkoutData ?? HealthConnectPayload.Read(capture?.BodyText);
                if (capture is not null && batch is not null)
                    result.Add(new WorkoutDelivery(capture.Id, batch));
                if (result.Count == WorkoutDataBatch.PageSize) break;
            }
            catch (FileNotFoundException) { } // Acknowledged or expired while listing.
        }
        return result;
    }

    public void Acknowledge(IEnumerable<string> ids)
    {
        Directory.CreateDirectory(_acknowledged);
        foreach (string id in ids)
        {
            var file = id + ".json";
            try { File.Move(Path.Combine(_directory, file), Path.Combine(_acknowledged, file)); }
            catch (FileNotFoundException) { } // Acknowledgement retries are idempotent.
            catch (DirectoryNotFoundException) when (!Directory.Exists(_directory)) { }
        }
    }

    public void Cleanup(DateTimeOffset now)
    {
        foreach (var path in Files(_acknowledged, "*.json")) File.Delete(path);
        foreach (var path in Files(_directory, "*.json").Concat(Files(_directory, "*.tmp")))
            if (Expired(path, now)) File.Delete(path);
    }

    private static bool Expired(string path, DateTimeOffset now) => File.GetLastWriteTimeUtc(path) <= now.UtcDateTime.AddMonths(-1);

    private static IEnumerable<string> Files(string directory, string pattern) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern) : [];
}
