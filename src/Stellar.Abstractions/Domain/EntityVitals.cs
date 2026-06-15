namespace Stellar.Abstractions.Domain;

/// <summary>
/// Live HP snapshot for an entity, sourced from <c>AttrCollection</c>
/// observations on the combat wire (<c>AttrHp</c>=11310, <c>AttrMaxHp</c>=11320).
/// Available for every entity in AOI — players, mobs, NPCs — not just party
/// members. Use <see cref="Stellar.Abstractions.Services.ICombatLookup.GetVitals"/>
/// to query.
/// </summary>
/// <param name="Hp">Last-known current HP. Zero when the entity has never been observed.</param>
/// <param name="MaxHp">Last-known max HP. Zero when the entity has never been observed or hasn't reported max yet.</param>
/// <param name="IsKnown">True once at least one AttrHp or AttrMaxHp observation has landed.</param>
public readonly record struct EntityVitals(long Hp, long MaxHp, bool IsKnown)
{
    /// <summary>Sentinel returned when no observation has been received for this entity yet.</summary>
    public static readonly EntityVitals Unknown = new(0, 0, false);
}
