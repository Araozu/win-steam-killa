using Shared;

namespace Service;

// The timer caches the last successful weekly result when a workout sync fails.
public interface IWorkoutCreditSource
{
    Task<int> GetWeeklyEarnedSecondsAsync(string weekStartLogicalDay, string todayLogicalDay, CancellationToken ct);
}

public sealed class NullWorkoutCreditSource : IWorkoutCreditSource
{
    public Task<int> GetWeeklyEarnedSecondsAsync(string weekStartLogicalDay, string todayLogicalDay, CancellationToken ct)
        => throw new InvalidOperationException("Workout credit source is not configured.");
}

internal sealed class LocalWorkoutCreditSource(WorkoutDataSync sync) : IWorkoutCreditSource
{
    public async Task<int> GetWeeklyEarnedSecondsAsync(string weekStartLogicalDay, string todayLogicalDay, CancellationToken ct)
    {
        var archive = await sync.SyncAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return WorkoutCredit.WeeklyDotaSeconds(archive.Sessions.Values, weekStartLogicalDay, todayLogicalDay);
    }
}

internal sealed class ConfiguredWorkoutCreditSource(WorkoutConnectionStore settings, HttpClient http,
    string archivePath, ILogger<WorkoutDataSync> log) : IWorkoutCreditSource
{
    public Task<int> GetWeeklyEarnedSecondsAsync(string weekStartLogicalDay, string todayLogicalDay, CancellationToken ct)
    {
        var connection = settings.Current;
        var sync = new WorkoutDataSync(http, connection.BaseUrl, connection.ApiToken, archivePath, log);
        return new LocalWorkoutCreditSource(sync).GetWeeklyEarnedSecondsAsync(weekStartLogicalDay, todayLogicalDay, ct);
    }
}

internal static class WorkoutCredit
{
    // Sessions started in [weekStart, today] (policy-clock logical days) earn
    // 3.3x their workout time, rounded down and capped at the weekly budget.
    // Overlapping sessions (the same workout saved twice, or nested records)
    // count their shared time once: each overlapping group earns its combined
    // span, never more than the durations its sessions report.
    public static int WeeklyDotaSeconds(IEnumerable<WorkoutSession> sessions, string weekStartLogicalDay, string todayLogicalDay)
    {
        var week = sessions
            .Where(session =>
            {
                string day = LogicalDay.ForUtc(session.Start.UtcDateTime);
                return string.CompareOrdinal(day, weekStartLogicalDay) >= 0 && string.CompareOrdinal(day, todayLogicalDay) <= 0;
            })
            .OrderBy(session => session.Start)
            .ToList();

        long workoutSeconds = 0;
        for (int i = 0; i < week.Count;)
        {
            var groupStart = week[i].Start;
            var groupEnd = week[i].End;
            long reported = 0;
            for (; i < week.Count && week[i].Start < groupEnd; i++)
            {
                if (week[i].End > groupEnd) groupEnd = week[i].End;
                reported += week[i].DurationSeconds;
            }
            workoutSeconds += Math.Min(reported, (long)Math.Floor((groupEnd - groupStart).TotalSeconds));
        }

        long dotaSeconds = workoutSeconds * 33 / 10;
        return (int)Math.Min(dotaSeconds, (long)Limits.DotaWeeklyBudget.TotalSeconds);
    }
}
