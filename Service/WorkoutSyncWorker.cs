using Microsoft.Extensions.Hosting;

namespace Service;

// Network I/O must never delay the independent 15-second enforcement loop.
public sealed class WorkoutSyncWorker : BackgroundService
{
    private readonly TimerEngine _engine;
    private readonly ILogger<WorkoutSyncWorker> _log;
    public WorkoutSyncWorker(TimerEngine engine, ILogger<WorkoutSyncWorker> log)
    {
        _engine = engine;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try
        {
            do
            {
                try { await _engine.RefreshWorkoutCreditsAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _log.LogWarning(ex, "workout sync failed"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
