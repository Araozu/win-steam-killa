using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Service;

internal sealed record WorkoutConnectionSettings(string BaseUrl, string ApiToken)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiToken);

    public static WorkoutConnectionSettings FromConfiguration(IConfiguration configuration) => new(
        Value(configuration, "Workout:BaseUrl", "WORKOUT_BASE_URL"),
        Value(configuration, "Workout:ApiToken", "WORKOUT_API_TOKEN"));

    private static string Value(IConfiguration configuration, string key, string alias) =>
        !string.IsNullOrWhiteSpace(configuration[key]) ? configuration[key]! : configuration[alias] ?? "";
}

internal sealed class WorkoutConnectionStore(
    string path, WorkoutConnectionSettings fallback, ILogger? log = null)
{
    private readonly object _lock = new();
    private WorkoutConnectionSettings _current = Load(path, fallback, log);

    public WorkoutConnectionSettings Current
    {
        get { lock (_lock) return _current; }
    }

    public string? Save(string? url, string? token)
    {
        if (url is not { Length: > 0 and <= 512 }
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return "Enter an HTTPS receiver URL without credentials, a query, or a fragment.";

        string normalizedUrl = uri.AbsoluteUri.TrimEnd('/');
        token = token?.Trim();
        if (token is { Length: > 512 } || (token is { Length: > 0 } && !IsBearerToken(token)))
            return "Enter the bearer token by itself, without the Bearer prefix.";

        lock (_lock)
        {
            if (string.IsNullOrEmpty(token) && !string.Equals(normalizedUrl, _current.BaseUrl.TrimEnd('/'), StringComparison.Ordinal))
                return "Enter the token for the new receiver URL.";
            string updatedToken = string.IsNullOrEmpty(token) ? _current.ApiToken : token;
            if (string.IsNullOrWhiteSpace(updatedToken)) return "Enter the receiver's bearer token.";

            var settings = new WorkoutConnectionSettings(normalizedUrl, updatedToken);
            string temp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, settings);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
                _current = settings;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.LogWarning("Workout settings could not be saved ({ErrorType}).", ex.GetType().Name);
                return "The service couldn't save the workout settings.";
            }
            finally
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return null;
    }

    private static bool IsBearerToken(string token)
    {
        string value = token.TrimEnd('=');
        return value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "-._~+/".Contains(c));
    }

    private static WorkoutConnectionSettings Load(string path, WorkoutConnectionSettings fallback, ILogger? log)
    {
        try
        {
            using var file = File.OpenRead(path);
            return JsonSerializer.Deserialize<WorkoutConnectionSettings>(file) is { IsConfigured: true } saved
                ? saved
                : fallback;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return fallback; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log?.LogWarning("Workout settings could not be loaded ({ErrorType}).", ex.GetType().Name);
            return fallback;
        }
    }
}
