using Shared;

namespace Service;

// The timer caches the last successful weekly result when a workout sync fails.
public interface IWorkoutCreditSource
{
    Task<WorkoutCreditTotals> GetEarnedAsync(string weekStartLogicalDay, string todayLogicalDay, CancellationToken ct);
}

public readonly record struct WorkoutCreditTotals(long WeekdaySeconds, long WeekendSeconds);

public sealed class NullWorkoutCreditSource : IWorkoutCreditSource
{
    public Task<WorkoutCreditTotals> GetEarnedAsync(string weekStartLogicalDay, string todayLogicalDay, CancellationToken ct)
        => throw new InvalidOperationException("Workout credit source is not configured.");
}

internal sealed class LocalWorkoutCreditSource(WorkoutDataSync sync) : IWorkoutCreditSource
{
    public async Task<WorkoutCreditTotals> GetEarnedAsync(string weekStartLogicalDay, string todayLogicalDay, CancellationToken ct)
    {
        var archive = await sync.SyncAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return WorkoutCredit.ForWeek(archive, weekStartLogicalDay, todayLogicalDay);
    }
}

internal sealed class ConfiguredWorkoutCreditSource(WorkoutConnectionStore settings, HttpClient http,
    string archivePath, ILogger<WorkoutDataSync> log) : IWorkoutCreditSource
{
    public Task<WorkoutCreditTotals> GetEarnedAsync(string weekStartLogicalDay, string todayLogicalDay, CancellationToken ct)
    {
        var connection = settings.Current;
        var sync = new WorkoutDataSync(http, connection.BaseUrl, connection.ApiToken, archivePath, log);
        return new LocalWorkoutCreditSource(sync).GetEarnedAsync(weekStartLogicalDay, todayLogicalDay, ct);
    }
}

internal static class WorkoutCredit
{
    public static WorkoutCreditTotals ForWeek(WorkoutArchive archive, string weekStartLogicalDay, string todayLogicalDay)
    {
        var weekdays = new List<WorkoutSession>();
        var weekend = new List<WorkoutSession>();
        foreach (var session in archive.Sessions.Values)
        {
            if (session.DurationSeconds <= 0 || session.End <= session.Start) continue;
            var start = PolicyClock.FromUtc(session.Start.UtcDateTime);
            string day = LogicalDay.For(start);
            if (string.CompareOrdinal(day, weekStartLogicalDay) < 0
                || string.CompareOrdinal(day, todayLogicalDay) > 0)
                continue;
            (Limits.IsWeekend(start.DayOfWeek) ? weekend : weekdays).Add(session);
        }
        long weekdayWorkoutSeconds = WorkoutSeconds(weekdays);
        long weekendWorkoutSeconds = WorkoutSeconds(weekend);
        if (weekdays.Count > 0 && weekend.Count > 0 && weekdays.Max(session => session.End) > weekend[0].Start)
        {
            // A workout that crosses Saturday midnight keeps its start-day
            // budget. A second record must not earn the overlap again.
            long combined = WorkoutSeconds([.. weekdays, .. weekend]);
            weekendWorkoutSeconds = Math.Min(weekendWorkoutSeconds, combined - weekdayWorkoutSeconds);
        }
        return new(weekdayWorkoutSeconds * 33 / 10, weekendWorkoutSeconds * 33 / 10);
    }

    // Within each budget, overlapping records count their shared span once.
    private static long WorkoutSeconds(List<WorkoutSession> sessions)
    {
        sessions.Sort((a, b) => a.Start.CompareTo(b.Start));
        long workoutSeconds = 0;
        for (int i = 0; i < sessions.Count;)
        {
            var groupStart = sessions[i].Start;
            var groupEnd = sessions[i].End;
            long reported = 0;
            for (; i < sessions.Count && sessions[i].Start < groupEnd; i++)
            {
                if (sessions[i].End > groupEnd) groupEnd = sessions[i].End;
                reported += sessions[i].DurationSeconds;
            }
            workoutSeconds += Math.Min(reported, (long)Math.Floor((groupEnd - groupStart).TotalSeconds));
        }

        return workoutSeconds;
    }
}
