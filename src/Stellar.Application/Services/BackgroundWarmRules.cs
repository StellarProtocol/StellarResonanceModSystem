namespace Stellar.Application.Services;

/// <summary>The Unity-free decisions of the background warm (pure, unit-tested).</summary>
internal static class BackgroundWarmRules
{
    /// <summary>Applies a warm must see before it may end. A BubbleElement measures once on its first apply and owes two
    /// "settle" measures after that (BubbleBinding.SettleApplies); a window parked right after its mount apply paid those
    /// two on its first real open (the 24 bubble measures of the measured 68.9 ms open frame).</summary>
    public const int SettleApplies = 3;

    /// <summary>A pre-warm parks its window once the background warm is done, or after <paramref name="maxTicks"/> (a
    /// warm that never finishes — a canvas drop, a live-updating bubble — must not keep a hidden window active forever).
    /// Never before <paramref name="mountTicks"/>, so the mount and its first apply always land.</summary>
    public static bool ShouldPark(int ticks, int mountTicks, int maxTicks, bool done)
        => ticks >= mountTicks && (done || ticks >= maxTicks);

    /// <summary>The window's own background work is finished.</summary>
    public static bool IsDone(bool prebuildIdle, int appliesSinceStart, int pendingMeasures)
        => prebuildIdle && appliesSinceStart >= SettleApplies && pendingMeasures == 0;

    /// <summary>A revealed (all units active) prebuild branch may be put away: an apply has run since the reveal ended
    /// (texts filled), and its background bubble measures are done — or it waited <paramref name="maxWait"/> applies
    /// (a bubble whose content changes every apply would otherwise hold it forever).</summary>
    public static bool RevealSettled(int appliesSinceReveal, int pendingMeasures, int maxWait)
        => appliesSinceReveal >= 1 && (pendingMeasures == 0 || appliesSinceReveal >= maxWait);
}

/// <summary>Did a pre-warmed window's content change while it was hidden (→ re-run its background warm, so its next open
/// is still a plain re-show)? Fed a cheap content signature from time to time. While the window is SHOWN its bindings keep
/// it current, so the signature just becomes the new baseline; while hidden, a signature different from the baseline is a
/// change (reported once — the baseline moves to it). The first sample is only a baseline. Pure, unit-tested.</summary>
internal sealed class ContentWatch
{
    private int _baseline;
    private bool _has;

    /// <summary>The signature the window's content was last applied with (meaningless before the first sample).</summary>
    public int Baseline => _baseline;

    /// <summary>Record <paramref name="signature"/> as what the window now shows (a warm just applied it).</summary>
    public void Reset(int signature) { _baseline = signature; _has = true; }

    /// <summary>One sample. True = the content changed while hidden: re-warm.</summary>
    public bool Changed(int signature, bool shown)
    {
        if (!_has || shown) { Reset(signature); return false; }
        if (signature == _baseline) return false;
        _baseline = signature;
        return true;
    }
}
