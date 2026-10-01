using System;
namespace Stellar.Application.Services;

/// <summary>
/// Capture / restore bookkeeping for ONE game setting the framework overrides. The game's value is captured
/// before the first write and restored when the override ends. A live value that no longer equals what we last
/// wrote means the game wrote it since (its quality grade re-applied, the settings panel): that value becomes the
/// new baseline, so a release restores what the game wants now, never a stale capture. Every decision is "write
/// only when the live value differs".
/// </summary>
internal sealed class LeverOverride<T> where T : struct
{
    private readonly Func<T, T, bool> _equal;
    private T? _baseline;
    private T? _lastWritten;

    public LeverOverride(Func<T, T, bool> equal) => _equal = equal;

    /// <summary>True while the game's value is captured (overriding, or a restore not yet written).</summary>
    public bool Holding => _baseline.HasValue;

    /// <summary>
    /// Given the live value and the wanted override (null = no override), returns the value to write, or null when
    /// nothing must be written. Updates the bookkeeping as if the returned write succeeds.
    /// </summary>
    public T? Decide(T current, T? target)
    {
        if (_lastWritten is T last && !_equal(current, last)) _baseline = current;   // the game wrote since
        if (target is T want)
        {
            _baseline ??= current;
            _lastWritten = want;
            return _equal(current, want) ? null : want;
        }
        if (_baseline is not T restore) return null;
        _baseline = null;
        _lastWritten = null;
        return _equal(current, restore) ? null : restore;
    }
}
