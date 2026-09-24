namespace Stellar.Abstractions.Domain;

/// <summary>
/// One active debuff's render state for a meter row's trailing 2×2 debuff block.
/// </summary>
/// <param name="IconTexture">Opaque <c>UnityEngine.Texture2D</c> handle (or null while loading / unavailable).</param>
/// <param name="IconUv">UV sub-rect of the icon within its atlas/texture (0..1, bottom-left origin).</param>
/// <param name="Stacks">Current stack/layer count; a "×N" badge is drawn only when &gt; 1.</param>
/// <param name="RemainFraction">Fraction of the debuff's duration still remaining: 1 = just applied (or permanent), 0 = expired. Drives the radial sweep (the renderer darkens the elapsed <c>1 - RemainFraction</c> arc).</param>
/// <param name="Present">True when this cell has a debuff to draw.</param>
public readonly record struct DebuffSlot(
    object? IconTexture,
    UvRect  IconUv,
    int     Stacks,
    float   RemainFraction,
    bool    Present)
{
    /// <summary>Empty cell — nothing to render.</summary>
    public static readonly DebuffSlot None = new(null, new UvRect(0f, 0f, 1f, 1f), 0, 1f, false);
}
