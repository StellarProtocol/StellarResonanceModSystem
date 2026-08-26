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

    /// <summary>
    /// True when this row was KEPT despite an AOI-disappear (<c>EDisappearNormal</c> — the entity left
    /// AOI while still alive elsewhere, e.g. a raid boss on another stage of a raid's one big
    /// multi-stage map) rather than being evicted. Before the 2026-08-26 raid-bosshp-capture-design
    /// fix, ANY disappear evicted the row, so callers used <see cref="IsKnown"/> flipping back to
    /// <see langword="false"/> as an AOI-presence proxy (e.g. "vitals unknown" == "left AOI", the
    /// signal a raid scripted-kill detector or stage-drain logic keys eviction on). That proxy no
    /// longer holds — a Normal-disappear leaves <see cref="IsKnown"/> <see langword="true"/> with
    /// stale-but-real data. Callers that need "still in AOI right now" must check
    /// <c>IsKnown &amp;&amp; !LeftAoi</c> instead of <c>IsKnown</c> alone. Cleared by the very next
    /// real vitals observation for this entity (any <c>AttrHp</c>/<c>AttrMaxHp</c>/<c>AttrMaxHpTotal</c>
    /// delta, including a MaxHp-only one). Defaults to <see langword="false"/>; init-only, same
    /// binary-compat rationale as <see cref="HasHpObservation"/>.
    /// </summary>
    public bool LeftAoi { get; init; }

    /// <summary>Sentinel returned when no observation has been received for this entity yet.</summary>
    public static readonly EntityVitals Unknown = new(0, 0, false);
}
