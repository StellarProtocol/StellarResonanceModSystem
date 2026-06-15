namespace Stellar.Abstractions.Domain;

/// <summary>
/// One equipped Battle Imagine's render state for a meter row's trailing icons.
/// </summary>
/// <param name="IconTexture">Opaque <c>UnityEngine.Texture2D</c> handle (or null while loading / unavailable).</param>
/// <param name="IconUv">UV sub-rect of the icon within its atlas/texture (0..1, bottom-left origin).</param>
/// <param name="CooldownFraction">Recharge progress of the next charge: 0 = ready, 1 = just used. Drives the radial sweep.</param>
/// <param name="RemainingSeconds">Whole seconds until the next charge is ready (0 when ready).</param>
/// <param name="ChargesAvailable">Currently usable charges.</param>
/// <param name="ChargeCount">Max charges (badge shown only when &gt; 1).</param>
/// <param name="Inferred">True when this is an observed-cast guess for another player (vs authoritative self data).</param>
public readonly record struct ImagineSlot(
    object? IconTexture,
    UvRect  IconUv,
    float   CooldownFraction,
    int     RemainingSeconds,
    int     ChargesAvailable,
    int     ChargeCount,
    bool    Inferred)
{
    /// <summary>Empty slot — nothing to render.</summary>
    public static readonly ImagineSlot None = new(null, new UvRect(0f, 0f, 1f, 1f), 0f, 0, 0, 0, false);

    /// <summary>True when this slot has an Imagine to draw.</summary>
    public bool HasImagine => IconTexture != null || ChargeCount > 0;
}
