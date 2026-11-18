using Shared;

namespace App;

// No UI dependencies: both freshness and countdowns can be checked without Windows.
internal sealed class TimerViewState(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _receivedAt;
    private long _nextRequest;
    private long _lastCompletedRequest;

    public PipeResponse? Last { get; private set; }
    public bool ServiceAvailable { get; private set; } = true;
    public double ElapsedSeconds => Last == null ? 0 : Math.Max(0, _clock.GetElapsedTime(_receivedAt).TotalSeconds);
    public bool IsCurrent => ServiceAvailable && Last != null && ElapsedSeconds <= 30;

    public long BeginRequest() => ++_nextRequest;

    // Requests are ordered by the client, so service restarts and clock changes
    // cannot make a fresh response look older than the current snapshot.
    public bool CompleteRequest(long request, PipeResponse? response)
    {
        if (request <= _lastCompletedRequest) return false;
        _lastCompletedRequest = request;
        ServiceAvailable = response != null;
        if (response != null)
        {
            Last = response;
            _receivedAt = _clock.GetTimestamp();
        }
        return true;
    }

    public TimerValues Project(DateTime localNow)
    {
        var r = Last ?? throw new InvalidOperationException("No service snapshot has been received.");
        double elapsed = ElapsedSeconds;
        double sessionMax = r.SessionMaxSeconds > 0 ? r.SessionMaxSeconds : Limits.SessionMax.TotalSeconds;
        double sessionElapsed = Math.Clamp(r.SessionElapsedSeconds, 0, sessionMax);
        double remaining = Math.Max(0, r.RemainingSeconds);
        double dotaRemaining = Math.Max(0, r.DotaRemainingSeconds);
        double windowLeft = r.WindowEndsLocal is { } end
            ? Math.Max(0, (end - localNow).TotalSeconds) : double.PositiveInfinity;
        bool accruing = r.Running && !r.InCooldown && !r.OutsidePlayHours && !r.Enforcing;
        if (accruing)
        {
            // Never extrapolate past a known session, daily, or window deadline.
            double played = Math.Min(Math.Min(elapsed, 30), Math.Min(remaining, sessionMax - sessionElapsed));
            if (r.WindowEndsLocal is { } windowEnd)
                played = Math.Min(played, Math.Max(0, (windowEnd - localNow).TotalSeconds + elapsed));
            remaining -= played;
            sessionElapsed += played;
            if (r.DotaRunning && !r.DotaEnforcing && !r.DotaForbidden)
                dotaRemaining = Math.Max(0, dotaRemaining - played);
        }

        // A new session is still limited to SessionMax, even while Steam is stopped.
        double sessionLeft = r.Running ? sessionMax - sessionElapsed : sessionMax;
        double playable = Math.Min(Math.Min(sessionLeft, remaining), windowLeft);
        if (r.DotaRunning)
            playable = Math.Min(playable, r.DotaEnforcing || r.DotaForbidden ? 0 : dotaRemaining);
        if (r.Enforcing || r.InCooldown || r.OutsidePlayHours) playable = 0;

        return new TimerValues(remaining, sessionElapsed, sessionMax, dotaRemaining,
            Math.Max(0, playable), Math.Max(0, r.CooldownRemainingSeconds - elapsed));
    }
}

internal readonly record struct TimerValues(double Remaining, double SessionElapsed,
    double SessionMax, double DotaRemaining, double Playable, double CooldownRemaining);
