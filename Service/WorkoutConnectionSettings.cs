using Microsoft.Extensions.Configuration;

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
