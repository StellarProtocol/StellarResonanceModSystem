using System;
namespace Stellar.Application.Services;

/// <summary>Result of one <see cref="LeverOverride{T}.Step"/>.</summary>
internal enum LeverOutcome
{
    /// <summary>The live value already matched; nothing was written.</summary>
    NoWrite,
    /// <summary>A write was made and the read-back shows it stuck.</summary>
    Wrote,
    /// <summary>A write was attempted but failed or did not stick (e.g. the game clamped it); retried next step.</summary>
    Failed,
}

/// <summary>
/// Capture / restore bookkeeping for ONE game setting the framework overrides. The game's value is captured
/// before the first write and restored when the override ends. A live value that no longer equals what we last
/// wrote means the game wrote it since (its quality grade re-applied, the settings panel): that value becomes the
/// new baseline, so a release restores what the game wants now, never a stale capture. Every write happens only
/// when the live value differs, and the bookkeeping commits only once the write is CONFIRMED (the backend reports
/// success and the read-back equals what we wrote). A failed or non-sticking write records the live value it left as
/// ours — so it is never adopted as a game baseline — and the next step retries it; a failed restore keeps the
/// capture.
/// </summary>
internal sealed class LeverOverride<T> where T : struct
{
    private readonly Func<T, T, bool> _equal;
    private T? _baseline;
    private T? _lastWritten;

    public LeverOverride(Func<T, T, bool> equal) => _equal = equal;

    /// <summary>True while the game's value is captured (overriding, or a restore not yet confirmed).</summary>
    public bool Holding => _baseline.HasValue;

    /// <summary>
    /// One reconcile step. <paramref name="current"/> = live value, <paramref name="target"/> = wanted override (null
    /// = no override). <paramref name="write"/> writes a value and returns whether the backend reported success plus
    /// the live value read back afterwards (null = unreadable; a successful write is then trusted).
    /// </summary>
    public LeverOutcome Step(T current, T? target, Func<T, (bool Ok, T? After)> write)
    {
        if (_lastWritten is T last && !_equal(current, last)) _baseline = current;   // the game wrote since
        if (target is T want)
        {
            _baseline ??= current;
            if (_equal(current, want)) { _lastWritten = want; return LeverOutcome.NoWrite; }
            return Write(want, current, write, confirmed: () => _lastWritten = want);
        }
        if (_baseline is not T restore) return LeverOutcome.NoWrite;
        if (_equal(current, restore)) { Release(); return LeverOutcome.NoWrite; }
        return Write(restore, current, write, confirmed: Release);
    }

    private LeverOutcome Write(T value, T current, Func<T, (bool Ok, T? After)> write, Action confirmed)
    {
        var (ok, after) = write(value);
        if (ok && (after is not T a || _equal(a, value)))
        {
            confirmed();
            return LeverOutcome.Wrote;
        }
        _lastWritten = after ?? current;   // whatever is live now came from our attempt, not from the game
        return LeverOutcome.Failed;
    }

    private void Release()
    {
        _baseline = null;
        _lastWritten = null;
    }
}
