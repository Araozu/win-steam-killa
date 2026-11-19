sealed record CaptureConfig(string ApiToken, string DataDir, string? CaptureToken = null, string? SyncToken = null)
{
    public const int MaxBodyBytes = 256 * 1024;

    public static CaptureConfig FromEnv() => new(
        Environment.GetEnvironmentVariable("API_TOKEN") ?? "",
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATA_DIR"))
            ? "/data" : Environment.GetEnvironmentVariable("DATA_DIR")!,
        Environment.GetEnvironmentVariable("CAPTURE_API_TOKEN"),
        Environment.GetEnvironmentVariable("SYNC_API_TOKEN"));

    public string TokenFor(WorkoutAccess access)
    {
        string? token = access == WorkoutAccess.Capture ? CaptureToken : SyncToken;
        return string.IsNullOrWhiteSpace(token) ? ApiToken : token;
    }
}

enum WorkoutAccess { Capture, Sync }
