using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Services;

/// <summary>Which effect layer a caster belongs to (spec D3: by who caused it). Caster 0 or an entity that is neither
/// a player nor a monster after summon-owner resolution → <see cref="VisibilityLayers.None"/> (never hidden).</summary>
internal sealed class EffectOwnerClassifier
{
    private readonly Func<EntityId> _local;
    private readonly Func<IReadOnlyList<PartyMember>> _party;
    private readonly Func<long, long> _topOwner;

    public EffectOwnerClassifier(Func<EntityId> local, Func<IReadOnlyList<PartyMember>> party, Func<long, long> topOwner)
    {
        _local = local;
        _party = party;
        _topOwner = topOwner;
    }

    public VisibilityLayers Classify(long casterUuid)
    {
        if (casterUuid == 0) return VisibilityLayers.None;
        var owner = new EntityId(_topOwner(casterUuid));
        var local = _local();
        if (!local.IsNone && owner == local) return VisibilityLayers.EffectsMine;
        if (owner.IsPlayer) return InParty(owner) ? VisibilityLayers.EffectsParty : VisibilityLayers.EffectsOthers;
        return owner.IsMonster ? VisibilityLayers.EffectsMonsters : VisibilityLayers.None;
    }

    private bool InParty(EntityId player)
    {
        foreach (var m in _party())
            if (!m.IsSelf && m.EntityId == player) return true;
        return false;
    }
}
