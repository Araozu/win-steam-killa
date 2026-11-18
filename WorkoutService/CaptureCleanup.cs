sealed class CaptureCleanup(CaptureStore store, ILogger<CaptureCleanup> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { store.Cleanup(DateTimeOffset.UtcNow); }
            catch (IOException ex) { log.LogWarning(ex, "Workout capture cleanup failed."); }
            catch (UnauthorizedAccessException ex) { log.LogWarning(ex, "Workout capture cleanup failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
