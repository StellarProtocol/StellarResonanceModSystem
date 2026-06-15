namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a single character attribute from the game table.</summary>
/// <param name="Id">Game-table attribute id.</param>
/// <param name="Name">Localised full attribute name (e.g. "Physical Attack").</param>
/// <param name="ShortName">Localised short attribute name for compact UI display.</param>
/// <param name="IconPath">Addressable path for the attribute's icon sprite.</param>
/// <param name="Group">Screen grouping for this attribute (Offensive / Defensive / etc.).</param>
public readonly record struct AttributeInfo(int Id, string Name, string ShortName, string IconPath, AttributeGroup Group)
{
    /// <summary>
    /// <c>FightAttrTable.AttrNumType</c> for this attribute's ×10 base: <c>0</c> = raw
    /// integer, <c>1</c> = per-10,000 percent (display value/100 with a % suffix),
    /// <c>2</c> = milliseconds (display value/1000 s). <c>-1</c> when no FightAttr row
    /// matched — callers should fall back to a heuristic. <c>-2</c> = catalogued non-stat
    /// attribute (identity / bookkeeping ids such as name or equip blobs — no displayable number).
    /// </summary>
    public int NumType { get; init; } = -1;

    /// <summary>zproto <c>EAttrType</c> member name (e.g. "AttrCri"); empty when unknown.</summary>
    public string EnumName { get; init; } = "";
}
