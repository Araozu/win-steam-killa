using System.Text;
using System.Text.Json;
using Shared;

sealed record RequestCapture(string Id, DateTimeOffset ReceivedAtUtc, string? ContentType,
    string UserAgent, int BodyLength, string? BodyText, string BodyBase64)
{
    public WorkoutDataBatch? WorkoutData { get; init; }
}

// captures/     uploads with workout data, queued for the PC until acknowledged
// diagnostics/  everything else (test pings, empty syncs, unknown formats)
// acknowledged/ delivered captures, deleted by the next cleanup
// Captures in captures/ and diagnostics/ expire after one calendar month.
sealed class CaptureStore
{
    private readonly string _pending;
    private readonly string _diagnostics;
    private readonly string _acknowledged;
    private readonly ILogger<CaptureStore>? _log;

    public CaptureStore(CaptureConfig config, ILogger<CaptureStore>? log = null)
    {
        _pending = Path.Combine(config.DataDir, "captures");
        _diagnostics = Path.Combine(config.DataDir, "diagnostics");
        _acknowledged = Path.Combine(config.DataDir, "acknowledged");
        _log = log;
    }

    public async Task<RequestCapture> SaveAsync(byte[] body, string? contentType, string userAgent, CancellationToken ct)
    {
        string? text;
        try { text = new UTF8Encoding(false, true).GetString(body); }
        catch (DecoderFallbackException) { text = null; }
        var workoutData = HealthConnectPayload.Read(text, out int skipped);
        var capture = new RequestCapture(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            contentType, userAgent, body.Length, text, Convert.ToBase64String(body))
        {
            WorkoutData = workoutData
        };
        if (skipped > 0)
            _log?.LogInformation("Capture {Id}: skipped {Skipped} record(s) from other apps or with invalid fields.", capture.Id, skipped);

        string directory = workoutData is null ? _diagnostics : _pending;
        Directory.CreateDirectory(directory);
        // IDs and filenames come from the server, never from phone input. Atomic
        // publication means readers only see completed captures. Keep every retry
        // so duplicate delivery and changing payloads can be inspected later.
        var path = Path.Combine(directory, capture.Id + ".json");
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
        foreach (var path in Files(_pending, "*.json").OrderBy(File.GetLastWriteTimeUtc))
        {
            ct.ThrowIfCancellationRequested();
            if (Expired(path, DateTimeOffset.UtcNow)) continue;
            RequestCapture? capture;
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                capture = await JsonSerializer.DeserializeAsync<RequestCapture>(file, cancellationToken: ct);
            }
            catch (FileNotFoundException) { continue; } // Acknowledged or expired while listing.
            // Captures saved before normalization was stored are parsed here once.
            var batch = capture?.WorkoutData ?? HealthConnectPayload.Read(capture?.BodyText);
            if (capture is not null && batch is not null)
            {
                result.Add(new WorkoutDelivery(capture.Id, batch));
                if (result.Count == WorkoutDataBatch.PageSize) break;
            }
            else
            {
                // Nothing to deliver: keep it for inspection, out of the queue.
                MoveIfPresent(path, _diagnostics);
            }
        }
        return result;
    }

    public void Acknowledge(IEnumerable<string> ids)
    {
        foreach (string id in ids)
            MoveIfPresent(Path.Combine(_pending, id + ".json"), _acknowledged); // Retries are idempotent.
    }

    public void Cleanup(DateTimeOffset now)
    {
        foreach (var path in Files(_acknowledged, "*.json")) File.Delete(path);
        foreach (var directory in new[] { _pending, _diagnostics })
            foreach (var path in Files(directory, "*.json").Concat(Files(directory, "*.tmp")))
                if (Expired(path, now)) File.Delete(path);
    }

    private static void MoveIfPresent(string path, string directory)
    {
        Directory.CreateDirectory(directory);
        try { File.Move(path, Path.Combine(directory, Path.GetFileName(path))); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static bool Expired(string path, DateTimeOffset now) => File.GetLastWriteTimeUtc(path) <= now.UtcDateTime.AddMonths(-1);

    private static IEnumerable<string> Files(string directory, string pattern) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern) : [];
}
