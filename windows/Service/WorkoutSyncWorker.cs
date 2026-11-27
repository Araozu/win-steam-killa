using Microsoft.Extensions.Hosting;
using Shared;
using System.Net;

namespace Service;

// The enforcement loop runs independently of network I/O.
internal sealed class WorkoutSyncWorker : BackgroundService
{
    private readonly TimerEngine _engine;
    private readonly ILogger<WorkoutSyncWorker> _log;
    private readonly WorkoutConnectionStore _settings;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private bool _pending;
    private bool _running;
    private string? _lastResult;

    public WorkoutSyncWorker(TimerEngine engine, ILogger<WorkoutSyncWorker> log, WorkoutConnectionStore settings)
    {
        _engine = engine;
        _log = log;
        _settings = settings;
    }

    public WorkoutSyncStatus Status()
    {
        lock (_lock)
        {
            var (lastSuccess, earned) = _engine.GetWorkoutCreditStatus();
            var settings = _settings.Current;
            return new(settings.BaseUrl, !string.IsNullOrWhiteSpace(settings.ApiToken),
                _running || _pending, lastSuccess, earned, _lastResult);
        }
    }

    public string? Save(string? url, string? token)
    {
        lock (_lock)
        {
            if (_running || _pending) return "Wait for the current sync to finish before saving.";
            string? error = _settings.Save(url, token);
            if (error is null) _lastResult = null;
            return error;
        }
    }

    public string? RequestSync()
    {
        lock (_lock)
        {
            if (!_settings.Current.IsConfigured) return "Save the receiver URL and token first.";
            if (!_running && !_pending)
            {
                _pending = true;
                _wake.Release();
            }
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            do
            {
                bool configured;
                lock (_lock)
                {
                    _wake.Wait(0);
                    _pending = false;
                    configured = _settings.Current.IsConfigured;
                    _running = configured;
                }

                if (configured)
                {
                    string? failure = null;
                    try
                    {
                        bool succeeded = await _engine.RefreshWorkoutCreditsAsync(stoppingToken,
                            ex => failure = DescribeFailure(ex)).ConfigureAwait(false);
                        lock (_lock) _lastResult = succeeded ? "Sync complete." : failure ?? "Sync failed.";
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "workout sync failed");
                        lock (_lock) _lastResult = "Sync failed while updating local workout credit.";
                    }
                    finally
                    {
                        lock (_lock) _running = false;
                    }
                }
                await _wake.WaitAsync(TimeSpan.FromMinutes(15), stoppingToken).ConfigureAwait(false);
            } while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private static string DescribeFailure(Exception error) => error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
            => "The receiver rejected the bearer token.",
        HttpRequestException { StatusCode: { } status } => $"The receiver returned HTTP {(int)status}.",
        HttpRequestException => "Couldn't connect to the receiver. Check its URL and your network.",
        OperationCanceledException => "The receiver didn't respond in time.",
        System.Text.Json.JsonException or InvalidDataException => "The receiver sent an unreadable workout feed.",
        IOException or UnauthorizedAccessException => "The service couldn't update the local workout archive.",
        _ => "Sync failed. Check the receiver settings and try again."
    };
}
