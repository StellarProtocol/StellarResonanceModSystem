namespace Stellar.Abstractions.Domain;

/// <summary>One worn cosmetic from the broadcast <c>AttrFashionData</c> attribute (id 201) —
/// available for ANY player in AOI (it is how the game renders other players' outfits).</summary>
/// <param name="Slot">Wardrobe slot code (wire <c>FashionInfo.slot</c>).</param>
/// <param name="FashionId">Cosmetic item id (resolves name/quality/icon via the item table).</param>
/// <param name="Dyes">The player's actual dye colours (converted from the wire's HSV triples); never null.</param>
public readonly record struct FashionEntry(int Slot, int FashionId, ColorRgba[] Dyes)
{
    /// <summary>Shared empty dye array for undyed pieces.</summary>
    public static readonly ColorRgba[] NoDyes = System.Array.Empty<ColorRgba>();

    /// <summary>Shared empty area array (parallel to <see cref="NoDyes"/>).</summary>
    public static readonly int[] NoAreas = System.Array.Empty<int>();

    /// <summary>The <c>EFashionColorAreaType</c> each entry of <see cref="Dyes"/> belongs to, in the same
    /// order (parallel array; <c>DyeAreas[i]</c> is the area of <c>Dyes[i]</c>). Absolute area codes
    /// 1..16 — Base1..4 (1-4), Socks1..4 (5-8), BaseEx1..4 (9-12), UnderWear1..4 (13-16). Empty (never
    /// null) when the source did not carry area keys — consumers then treat <see cref="Dyes"/> positionally.
    /// Init-only so it is optional at construction, keeping the primary constructor binary-compatible.</summary>
    public int[] DyeAreas { get; init; } = NoAreas;
}
