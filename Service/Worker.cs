using System.Diagnostics;

namespace Service;

public sealed class Worker : BackgroundService
{
    private readonly TimerEngine _engine;
    private readonly PipeServer _pipe;
    private readonly ILogger<Worker> _log;
    private static readonly TimeSpan WorkoutSyncInterval = TimeSpan.FromMinutes(15);
    private const string EventSource = "SteamTimer";

    public Worker(TimerEngine engine, PipeServer pipe, ILogger<Worker> log)
    {
        _engine = engine;
        _pipe = pipe;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("SteamTimer service starting.");
        var pipeTask = _pipe.RunAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        // Immediate tick on boot so reboot-resume kills fast if past deadline.
        TickOnce();
        // Immediate workout sync on boot so earned Dota credit applies without
        // waiting for the first 15-minute interval (fail-closed until then).
        await SyncWorkoutOnceAsync(stoppingToken).ConfigureAwait(false);
        var lastWorkoutSync = DateTime.UtcNow;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                TickOnce();
                if (DateTime.UtcNow - lastWorkoutSync >= WorkoutSyncInterval)
                {
                    lastWorkoutSync = DateTime.UtcNow;
                    await SyncWorkoutOnceAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        await pipeTask;
    }

    private void TickOnce()
    {
        try
        {
            var notes = _engine.Tick(_log);
            foreach (var (title, body) in notes)
                WriteEvent($"{title} — {body}", EventLogEntryType.Information);
        }
        catch (Exception ex) { _log.LogWarning(ex, "tick failed"); }
    }

    private async Task SyncWorkoutOnceAsync(CancellationToken ct)
    {
        try { await _engine.RefreshWorkoutCreditsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "workout sync failed"); }
    }

    private static void WriteEvent(string msg, EventLogEntryType type)
    {
        try
        {
            const string logName = "Application";
            if (!EventLog.SourceExists(EventSource))
                EventLog.CreateEventSource(EventSource, logName);
            EventLog.WriteEntry(EventSource, msg, type);
        }
        catch { }
    }
}
