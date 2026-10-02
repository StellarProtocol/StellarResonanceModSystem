namespace Stellar.Infrastructure.Game;

/// <summary>The combat-freeze evidence sampler's schedule and log budget (owner report 2026-10-02: frozen monsters keep
/// animating, sliding and casting in combat; one owner fight must yield the root cause). Diagnostics only: constructed with
/// <c>enabled = StellarDiagnostics.IsEnabled</c>, and when not enabled every query answers "no" and nothing is counted —
/// the sampler is inert (regression <c>freeze_combat_diag_inert_when_diagnostics_off</c>). Per freeze: a sample every
/// <see cref="IntervalMs"/> (2 Hz) for at most <see cref="LifetimeMs"/> (60 s), at most <see cref="LinesPerSecond"/>
/// sampler lines in any one-second window, and at most <see cref="EventLines"/> event lines (despawns, first hook hits).
/// Pure (unit-tested).</summary>
internal sealed class FreezeDiagClock
{
    internal const long IntervalMs = 500;
    internal const long LifetimeMs = 60_000;
    internal const int LinesPerSecond = 20;
    internal const int EventLines = 80;
    /// <summary>Entities sampled per sample (the nearest monsters / bosses within the radius).</summary>
    internal const int MaxEntities = 20;
    /// <summary>Entity lines logged per sample: two samples a second, one summary line each and one effects line a second
    /// stays inside <see cref="LinesPerSecond"/> (2 × (1 + 8) + 1 = 19).</summary>
    internal const int EntityLinesPerSample = 8;
    /// <summary>Sampling radius around the camera or the local player.</summary>
    internal const float Radius = 40f;

    private readonly bool _enabled;
    private long _startMs, _nextMs, _windowStartMs;
    private int _windowLines, _eventLines;

    public FreezeDiagClock(bool enabled) => _enabled = enabled;

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/> (never when not enabled).</summary>
    public bool Active { get; private set; }

    /// <summary>Samples taken this freeze (the next sample's index).</summary>
    public int Samples { get; private set; }

    /// <summary>Sampler lines refused by the budget this freeze.</summary>
    public int Dropped { get; private set; }

    /// <summary>Starts one freeze's sampling at <paramref name="nowMs"/>; the first sample is due at once.</summary>
    public void Start(long nowMs)
    {
        Active = _enabled;
        _startMs = _nextMs = _windowStartMs = nowMs;
        _windowLines = _eventLines = Samples = Dropped = 0;
    }

    public void Stop() => Active = false;

    /// <summary>Milliseconds since <see cref="Start"/>.</summary>
    public long Elapsed(long nowMs) => nowMs - _startMs;

    /// <summary>True once the freeze outlived <see cref="LifetimeMs"/> (sampling stops; despawn lines too).</summary>
    public bool Expired(long nowMs) => Elapsed(nowMs) > LifetimeMs;

    /// <summary>Whether the sampler still needs late frames this freeze.</summary>
    public bool WantsFrames(long nowMs) => Active && !Expired(nowMs);

    /// <summary>True — and the next sample is scheduled — when a sample is due at <paramref name="nowMs"/>.</summary>
    public bool Due(long nowMs)
    {
        if (!WantsFrames(nowMs) || nowMs < _nextMs) return false;
        _nextMs = nowMs + IntervalMs;
        Samples++;
        return true;
    }

    /// <summary>Takes one sampler line from the rolling one-second budget; false (and counted as dropped) when spent.</summary>
    public bool TakeLine(long nowMs)
    {
        if (!Active) return false;
        if (nowMs - _windowStartMs >= 1000) { _windowStartMs = nowMs; _windowLines = 0; }
        if (_windowLines >= LinesPerSecond) { Dropped++; return false; }
        _windowLines++;
        return true;
    }

    /// <summary>Takes one event line (despawn, first hit) from the per-freeze cap, inside the lifetime.</summary>
    public bool TakeEvent(long nowMs)
    {
        if (!Active || Expired(nowMs) || _eventLines >= EventLines) return false;
        _eventLines++;
        return true;
    }
}
