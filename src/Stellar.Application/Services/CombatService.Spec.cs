using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Application.Services;

/// <summary>
/// <c>ICombatSpec</c> — buff-first spec resolution (spec-from-talent-buffs, 2026-09-26). The resolution rules
/// live in <see cref="TalentSpecResolver"/>; this partial feeds it (cast spec from damage events) and turns its
/// real value changes into <see cref="CombatEvent.SpecChanged"/> on the drain.
/// </summary>
internal sealed partial class CombatService
{
    public int GetSubProfession(EntityId entityId) => _spec.GetSubProfession(entityId);

    public bool TryGetTalentSpec(EntityId entityId, out int subProfessionId)
        => _spec.TryGetTalentSpec(entityId, out subProfessionId);

    // Cast FALLBACK (last-seen-wins): recognise the caster's spec from the CAST skill id — the ZDPS-parity
    // method, used for entities with no talent root buff. Only player sources can have a spec;
    // SubProfessionFromSkill already returns null for non-spec skills, so the IsPlayer gate just avoids work.
    // A talent-derived spec is NOT overwritten: the resolver keeps the two sources apart.
    private void AccumulateSpec(CombatEvent evt)
    {
        if (evt is not CombatEvent.DamageDealt d) return;
        if (d.SourceId.IsNone || !d.SourceId.IsPlayer) return;
        if (ProfessionSpecs.SubProfessionFromSkill(d.SkillId) is { } sub && _entities.SetSubProfession(d.SourceId, sub))
            _spec.MarkDirty(d.SourceId, d.TimestampMs);
    }

    // Re-resolve entities whose inputs changed since the last drain and publish only REAL value changes.
    // Returns true when events were enqueued (the caller drains them in the same frame).
    private bool PublishSpecChanges()
    {
        var changes = _spec.CollectChanges();
        if (changes is null) return false;
        for (var i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            DiagSpecChange(c);
            EnqueueEvent(c);
        }
        return true;
    }
}
