using System.Globalization;
using System.Text.Json;
using Shared;

namespace Service;

// Weekly workout credit backing workout-gated Dota (v7).
// Production source sums WorkoutService daily /api/credit dotaSeconds for each
// logical day in the current Mon-03:00 week. Failures keep the cached value:
// callers only apply successful fetches, so offline/transient errors never
// zero out previously earned time. Fresh state (0 earned) is fail-closed.
public interface IWorkoutCreditSource
{
    Task<int> GetWeeklyEarnedSecondsAsync(string weekStartLogicalDay, string todayLogicalDay, CancellationToken ct);
}

// Unconfigured service: never treat missing configuration as a successful sync.
public sealed class NullWorkoutCreditSource : IWorkoutCreditSource
{
    public Task<int> GetWeeklyEarnedSecondsAsync(string weekStartLogicalDay, string todayLogicalDay, CancellationToken ct)
        => throw new InvalidOperationException("Workout credit source is not configured.");
}

// HTTP source against WorkoutService (/api/credit?day=yyyy-MM-dd, Bearer).
public sealed class HttpWorkoutCreditSource : IWorkoutCreditSource
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiToken;

    public HttpWorkoutCreditSource(HttpClient http, string baseUrl, string apiToken)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiToken = apiToken;
    }

    public async Task<int> GetWeeklyEarnedSecondsAsync(string weekStartLogicalDay, string todayLogicalDay, CancellationToken ct)
    {
        if (!LogicalDay.TryParse(weekStartLogicalDay, out var weekStart)
            || !LogicalDay.TryParse(todayLogicalDay, out var today))
            return 0;
        long total = 0;
        for (var d = weekStart.Date; d <= today.Date; d = d.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            string day = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            total += Math.Min(await GetDayEarnedAsync(day, ct).ConfigureAwait(false),
                (long)Limits.DotaWeeklyBudget.TotalSeconds);
            if (total >= (long)Limits.DotaWeeklyBudget.TotalSeconds)
                return (int)Limits.DotaWeeklyBudget.TotalSeconds;
        }
        return (int)Math.Min(total, (long)Limits.DotaWeeklyBudget.TotalSeconds);
    }

    private async Task<long> GetDayEarnedAsync(string day, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            _baseUrl + "/api/credit?day=" + day);
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("day", out var responseDay)
            || responseDay.ValueKind != JsonValueKind.String || responseDay.GetString() != day
            || !root.TryGetProperty("dotaSeconds", out var el)
            || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v) || v < 0)
            throw new JsonException("Invalid workout credit response.");
        return v;
    }
}
