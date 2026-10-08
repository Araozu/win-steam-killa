using System.Diagnostics;
using System.Security.Cryptography;

namespace App.Game;

internal enum FlightPhase { Boarding, Flying, Crashed }

// All timing is monotonic, not frame-based. A late frame/click evaluates the
// actual crash boundary first; automatic cash-out resolves in event-time order.
internal sealed class CrashGame
{
    public const decimal MaximumMultiplier = 100;
    public const double BoardingSeconds = 5;
    public const double ResultSeconds = 5;
    public const double GrowthRate = 0.16;
    private readonly Func<double> _seconds;
    private readonly Func<decimal> _drawCrash;
    private FlightTicket? _ticket;
    private double _launchAt;
    private double _crashedAt;
    private decimal _crashAt;

    public FlightWallet Wallet { get; }
    public Guid RoundId { get; private set; }
    public FlightPhase Phase { get; private set; }
    public decimal Multiplier { get; private set; } = 1;
    public decimal CashedAt { get; private set; }
    public decimal Payout { get; private set; }
    public decimal Bet => _ticket?.Bet ?? 0;
    public bool HasBet => _ticket != null;
    public bool HasLiveBet => Phase == FlightPhase.Flying && HasBet && CashedAt == 0;
    public double FlightSeconds => Math.Max(0, _seconds() - _launchAt);
    public double BoardingLeft => Phase == FlightPhase.Boarding ? Math.Clamp(_launchAt - _seconds(), 0, BoardingSeconds) : 0;
    public double CrashAge => Phase == FlightPhase.Crashed ? Math.Max(0, _seconds() - _crashedAt) : 0;

    public CrashGame(FlightWallet wallet, Func<double>? seconds = null, Func<decimal>? drawCrash = null)
    {
        Wallet = wallet;
        _seconds = seconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _drawCrash = drawCrash ?? DrawCrash;
        BeginBoarding(_seconds());
    }

    // Joining never starts/restarts the clock. The optional expected round ID
    // prevents a delayed UI click from booking a different flight.
    public bool Board(decimal bet, decimal autoCashout, DateTime localNow, Guid? expectedRound = null)
    {
        Update();
        if (Phase != FlightPhase.Boarding || HasBet || expectedRound.HasValue && expectedRound != RoundId)
            return false;
        Wallet.RefreshDailyGrant(localNow);
        _ticket = Wallet.PlaceBet(bet, _crashAt, autoCashout);
        return true;
    }

    private void BeginBoarding(double now)
    {
        _crashAt = _drawCrash();
        if (_crashAt < 1 || _crashAt > MaximumMultiplier || decimal.Round(_crashAt, 2) != _crashAt)
            throw new InvalidOperationException("Invalid flight crash point.");
        RoundId = Guid.NewGuid();
        _ticket = null;
        _launchAt = now + BoardingSeconds;
        Multiplier = 1;
        CashedAt = 0;
        Payout = 0;
        Phase = FlightPhase.Boarding;
    }

    public void Update()
    {
        double now = _seconds();
        if (Phase == FlightPhase.Crashed)
        {
            // After a suspended app/late tick, settle the actual flight and
            // give a fresh boarding window instead of replaying missed rounds.
            if (now >= _crashedAt + ResultSeconds) BeginBoarding(now);
            return;
        }
        double elapsed = now - _launchAt;
        if (elapsed < 0) return;
        Phase = FlightPhase.Flying;
        double crashTime = _launchAt + SecondsFor(_crashAt);
        decimal automatic = _ticket?.AutoCashout ?? 0;
        if (CashedAt == 0 && automatic > 0 && automatic < _crashAt && now >= _launchAt + SecondsFor(automatic))
            Collect(automatic);
        // Flights capped at 100x collect any remaining bet automatically.
        if (now >= crashTime)
        {
            Multiplier = _crashAt;
            if (_crashAt == MaximumMultiplier && HasLiveBet) Collect(MaximumMultiplier);
            if (_ticket != null) Wallet.Finish(_ticket.Id);
            else Wallet.RecordSpectatorFlight(_crashAt);
            _crashedAt = crashTime;
            Phase = FlightPhase.Crashed;
            return;
        }
        // Floating-point conversion must never display/collect the crash value
        // a fraction of a tick before its time boundary.
        Multiplier = Math.Min(_crashAt - 0.01m, decimal.Floor((decimal)Math.Exp(GrowthRate * elapsed) * 100) / 100);
    }

    public bool CashOut(Guid? expectedRound = null)
    {
        Update();
        if (!HasLiveBet || expectedRound.HasValue && expectedRound != RoundId) return false;
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
