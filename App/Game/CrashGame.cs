using System.Diagnostics;
using System.Security.Cryptography;

namespace App.Game;

internal enum FlightPhase { Ready, Countdown, Flying, Crashed }

// All timing is monotonic, not frame-based. A late frame/click evaluates the
// actual crash boundary first; automatic cash-out resolves in event-time order.
internal sealed class CrashGame
{
    public const decimal MaximumMultiplier = 100;
    public const double CountdownSeconds = 2.8;
    public const double GrowthRate = 0.16;
    private readonly Func<double> _seconds;
    private readonly Func<decimal> _drawCrash;
    private FlightTicket? _ticket;
    private double _started;

    public FlightWallet Wallet { get; }
    public FlightPhase Phase { get; private set; }
    public decimal Multiplier { get; private set; } = 1;
    public decimal CashedAt { get; private set; }
    public decimal Payout { get; private set; }
    public decimal Bet => _ticket?.Bet ?? 0;
    public bool HasLiveBet => Phase == FlightPhase.Flying && CashedAt == 0;
    public double FlightSeconds => Math.Max(0, _seconds() - _started - CountdownSeconds);
    public double CountdownLeft => Math.Max(0, CountdownSeconds - (_seconds() - _started));
    public double CrashAge => Phase == FlightPhase.Crashed && _ticket != null
        ? Math.Max(0, FlightSeconds - SecondsFor(_ticket.CrashAt)) : 0;

    public CrashGame(FlightWallet wallet, Func<double>? seconds = null, Func<decimal>? drawCrash = null)
    {
        Wallet = wallet;
        _seconds = seconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _drawCrash = drawCrash ?? DrawCrash;
    }

    public void Start(decimal bet, decimal autoCashout, DateTime localNow)
    {
        if (Phase is FlightPhase.Countdown or FlightPhase.Flying)
            throw new InvalidOperationException("Wait for this flight to finish.");
        Wallet.RefreshDailyGrant(localNow);
        var ticket = Wallet.PlaceBet(bet, _drawCrash(), autoCashout);
        _ticket = ticket;
        _started = _seconds();
        Multiplier = 1;
        CashedAt = 0;
        Payout = 0;
        Phase = FlightPhase.Countdown;
    }

    public void Update()
    {
        if (_ticket == null || Phase is FlightPhase.Ready or FlightPhase.Crashed) return;
        double elapsed = _seconds() - _started - CountdownSeconds;
        if (elapsed < 0) return;
        Phase = FlightPhase.Flying;
        double crashTime = SecondsFor(_ticket.CrashAt);
        decimal automatic = _ticket.AutoCashout;
        if (CashedAt == 0 && automatic > 0 && automatic < _ticket.CrashAt && elapsed >= SecondsFor(automatic))
            Collect(automatic);
        // Flights capped at 100x collect any remaining bet automatically.
        if (elapsed >= crashTime)
        {
            Multiplier = _ticket.CrashAt;
            if (_ticket.CrashAt == MaximumMultiplier && CashedAt == 0) Collect(MaximumMultiplier);
            Wallet.Finish(_ticket.Id);
            Phase = FlightPhase.Crashed;
            return;
        }
        // Floating-point conversion must never display/collect the crash value
        // a fraction of a tick before its time boundary.
        Multiplier = Math.Min(_ticket.CrashAt - 0.01m, decimal.Floor((decimal)Math.Exp(GrowthRate * elapsed) * 100) / 100);
    }

    public bool CashOut()
    {
        Update();
        if (!HasLiveBet) return false;
        Collect(Multiplier);
        return true;
    }

    private void Collect(decimal multiplier)
    {
        decimal payout = Wallet.CashOut(_ticket!.Id, multiplier);
        CashedAt = multiplier;
        Payout = payout;
    }

    public static double SecondsFor(decimal multiplier) => Math.Log((double)multiplier) / GrowthRate;

    internal static decimal CrashForSample(double uniform)
    {
        if (!double.IsFinite(uniform) || uniform < 0 || uniform >= 1)
            throw new ArgumentOutOfRangeException(nameof(uniform));
        return (decimal)Math.Clamp(Math.Floor(0.97 / (1 - uniform) * 100) / 100, 1, (double)MaximumMultiplier);
    }

    private static decimal DrawCrash()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        // 53 random bits, [0,1). No predictable Random seed or frame dependence.
        double uniform = (BitConverter.ToUInt64(bytes) >> 11) / 9007199254740992d;
        return CrashForSample(uniform);
    }
}
