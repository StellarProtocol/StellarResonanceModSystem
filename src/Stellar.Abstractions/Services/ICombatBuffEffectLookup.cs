using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Per-buff-instance "effect ids" decoded off the combat wire — the extra payload a buff instance carries that
/// <see cref="ActiveBuff"/> does not: the <c>BuffEffectLogicPlayEffect.effect_id</c> of its PlayEffect logic
/// entries, or the values of a <c>BuffEventCustomize</c> event (slot / lock codes). Raid mechanics encode
/// per-player meaning there (e.g. a slash order, a colour pattern). A base of <see cref="ICombatLookup"/>, so it
/// is reached as <c>services.CombatLookup.TryGetBuffEffectIds(...)</c>; it is a separate interface only because
/// <see cref="ICombatLookup"/> is at the 8-member cap. Needs framework ≥ 2.30.0.
/// </summary>
public interface ICombatBuffEffectLookup
{
    /// <summary>
    /// Latest effect ids of buff instance <paramref name="buffUuid"/> (<see cref="ActiveBuff.BuffUuid"/>) on
    /// <paramref name="entityId"/>. Update rules per <c>BuffEffect</c> (same as resonance-logs-cn): an AddBuff's
    /// embedded <c>BuffInfo</c> PlayEffect ids, then the effect's own PlayEffect ids when it carries an
    /// AddBuff/BuffChange entry, then a Customize event's values — each non-empty one replaces the previous; a
    /// buff removal drops them. Keyed by the entity part of the uuid (<c>Value &gt;&gt; 16</c>), so the local
    /// player's client-flagged uuid and the wire uuid both match. Cleared on scene change. Returns false (and an
    /// empty list) when nothing is known. Thread-safe and allocation-free; the returned list must not be mutated.
    /// </summary>
    bool TryGetBuffEffectIds(EntityId entityId, int buffUuid, out IReadOnlyList<int> effectIds);
}
