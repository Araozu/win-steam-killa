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
        long workoutSeconds = 0;
        foreach (var session in archive.Sessions.Values)
        {
            ct.ThrowIfCancellationRequested();
            string day = LogicalDay.ForLocal(session.Start.LocalDateTime);
            if (string.CompareOrdinal(day, weekStartLogicalDay) >= 0 && string.CompareOrdinal(day, todayLogicalDay) <= 0)
                workoutSeconds += session.DurationSeconds;
        }

        // Apply 3.3× to the weekly total and round down to a whole second.
        long dotaSeconds = workoutSeconds * 33 / 10;
        return (int)Math.Min(dotaSeconds, (long)Limits.DotaWeeklyBudget.TotalSeconds);
    }
}
