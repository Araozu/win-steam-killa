sealed record CaptureConfig(string ApiToken, string DataDir)
{
    public const int MaxBodyBytes = 256 * 1024;

    public static CaptureConfig FromEnv() => new(
        Environment.GetEnvironmentVariable("API_TOKEN") ?? "",
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATA_DIR"))
            ? "/data" : Environment.GetEnvironmentVariable("DATA_DIR")!);
}
