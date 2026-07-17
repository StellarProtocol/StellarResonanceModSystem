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
    /// <summary>
    /// True once a REAL current-HP value has been observed for this entity (an <c>AttrHp</c>
    /// carrying <c>hp &gt;= 0</c>, including 0 = dead). A MaxHp-only observation leaves this
    /// <see langword="false"/> while <see cref="IsKnown"/> is already <see langword="true"/> —
    /// such an entity is "alive, HP unknown", NOT dead. Death inference (e.g. a meter's dead
    /// styling, wipe detection) must require this flag before reading <see cref="Hp"/> &lt;= 0
    /// as death. Init-only (not a constructor parameter) so plugins compiled against older
    /// Abstractions keep binary compatibility.
    /// </summary>
    public bool HasHpObservation { get; init; }

    /// <summary>Sentinel returned when no observation has been received for this entity yet.</summary>
    public static readonly EntityVitals Unknown = new(0, 0, false);
}
