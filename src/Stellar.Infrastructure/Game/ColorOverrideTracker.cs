namespace Stellar.Infrastructure.Game;

/// <summary>What a colour binding does this poll.</summary>
internal enum ColorStep
{
    /// <summary>Leave the colour alone (no override now, none before — the default / a reskin stands).</summary>
    None,
    /// <summary>Apply the Color func's value (every poll while it returns one, so a reskin never wins).</summary>
    Override,
    /// <summary>The func just went back to null: put the element's default colour back, once.</summary>
    RestoreDefault,
}

/// <summary>
/// Pure state for a <c>Func&lt;ColorRgba?&gt;</c> colour binding (TextElement / PillElement Color). Without it a
/// func flipping from a colour back to null left the last override painted — measured in game: a pill kept its
/// warning colour after unpinning (Photo Studio fw fix round). Unity-free so it is unit-tested directly.
/// </summary>
internal struct ColorOverrideTracker
{
    private bool _overridden;

    public ColorStep Next(bool hasOverride)
    {
        if (hasOverride) { _overridden = true; return ColorStep.Override; }
        if (!_overridden) return ColorStep.None;
        _overridden = false;
        return ColorStep.RestoreDefault;
    }
}
