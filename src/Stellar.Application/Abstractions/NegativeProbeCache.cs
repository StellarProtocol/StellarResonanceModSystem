using System;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Bounded negative-result cache for one probed reflection target, replacing a PERMANENT latch —
/// see <c>docs/il2cpp-probing-safety.md</c> § "negative-cache races": a negative verdict must expire and be
/// re-tried, never poison a session forever (rule 3 there: "bound every negative verdict with a TTL"). While
/// <see cref="IsSuppressed"/>, a probe should skip re-resolving and just report unavailable; once the TTL
/// elapses, the next probe call tries the reflection lookup again. <see cref="Reset"/> forgets everything
/// immediately regardless of the TTL — call it on a scene-change / hot-update-ready signal (rule 4: "give the
/// caller an explicit forget-this hook, fired on session/scene boundaries"). Pure given its injected clock — no
/// I/O, no reflection, no game types — so it is directly unit-testable with a fake clock.
/// </summary>
internal sealed class NegativeProbeCache
{
    private readonly Func<long> _nowMs;
    private readonly long _ttlMs;
    private bool _negative;
    private long _markedAtMs;
    private bool _warned;

    /// <param name="nowMs">Monotonic milliseconds clock (production: <c>() => Environment.TickCount64</c>; a
    /// fake incrementing counter in tests).</param>
    /// <param name="ttlMs">How long a negative verdict is trusted before the next probe re-resolves.</param>
    public NegativeProbeCache(Func<long> nowMs, long ttlMs)
    {
        _nowMs = nowMs;
        _ttlMs = ttlMs;
    }

    /// <summary>True while a probe should be SKIPPED — marked negative and the TTL has not yet elapsed.</summary>
    public bool IsSuppressed => _negative && _nowMs() - _markedAtMs < _ttlMs;

    /// <summary>
    /// Marks a fresh definitive negative and (re)starts the TTL. Returns true only the FIRST time this happens
    /// since construction or the last <see cref="Reset"/>/<see cref="MarkRecovered"/> — the caller logs its one
    /// Warning only when this returns true.
    /// </summary>
    public bool MarkNegative()
    {
        _negative = true;
        _markedAtMs = _nowMs();
        var firstWarning = !_warned;
        _warned = true;
        return firstWarning;
    }

    /// <summary>
    /// Marks the target as resolved. Returns true only when this is a recovery from a previously WARNED
    /// negative — the caller logs its one recovery Info only when this returns true (a first-ever successful
    /// resolution, never having been negative, returns false: nothing to recover FROM).
    /// </summary>
    public bool MarkRecovered()
    {
        var recovered = _warned;
        _negative = false;
        _warned = false;
        return recovered;
    }

    /// <summary>Forgets everything unconditionally — the next probe re-resolves from scratch and, if it fails
    /// again, warns again (a fresh incident after a scene change / hot-update-ready deserves its own report).</summary>
    public void Reset()
    {
        _negative = false;
        _warned = false;
    }
}
