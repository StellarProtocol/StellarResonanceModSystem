namespace Stellar.Abstractions.Domain;

/// <summary>One worn cosmetic from the broadcast <c>AttrFashionData</c> attribute (id 201) —
/// available for ANY player in AOI (it is how the game renders other players' outfits).</summary>
/// <param name="Slot">Wardrobe slot code (wire <c>FashionInfo.slot</c>).</param>
/// <param name="FashionId">Cosmetic item id (resolves name/quality/icon via the item table).</param>
/// <param name="Dyes">The player's actual dye colours (converted from the wire's HSV triples), up to 4; never null.</param>
public readonly record struct FashionEntry(int Slot, int FashionId, ColorRgba[] Dyes)
{
    /// <summary>Shared empty dye array for undyed pieces.</summary>
    public static readonly ColorRgba[] NoDyes = System.Array.Empty<ColorRgba>();
}
