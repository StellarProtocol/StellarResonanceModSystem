namespace Stellar.Abstractions.Domain;

/// <summary>One numeric attribute of an entity as the wire delivered it: the <c>FightAttrTable</c> id and
/// its absolute value (basis points for the <c>万分比</c> attrs, flat points otherwise).</summary>
/// <param name="AttrId">The attribute id (e.g. 11710 crit chance, 12670 generic damage bonus).</param>
/// <param name="Value">The absolute value after this update — never a delta.</param>
public readonly record struct AttrValue(int AttrId, long Value);
