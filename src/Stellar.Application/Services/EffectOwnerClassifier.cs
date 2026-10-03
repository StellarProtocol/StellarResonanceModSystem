using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Services;

/// <summary>Which effect layer a caster belongs to (spec D3: by who caused it). Caster 0 or an entity that is neither
/// a player nor a monster after summon-owner resolution → <see cref="VisibilityLayers.None"/> (never hidden).
/// <para>Task 10 ("ask the game who owns it"): a caster still unresolved after the <see cref="SummonOwnerIndex"/> (a pet
/// whose owner no <see cref="CombatEvent.EntitySummonAppeared"/> announced — markers 1024 / 192) is put to the optional
/// <c>askGameOwner</c> fallback, which reads that entity's own summoner from the game (0 = unknown). A non-zero answer
/// is recorded into the index (so the next classification of that caster never asks again) and classified through the
/// normal path; an answer that is itself an unresolved summon is asked again, at most <see cref="MaxAsks"/> times in
/// total. No answer → None (never guessed).</para></summary>
internal sealed class EffectOwnerClassifier
{
    private const int MaxAsks = 3;
    private readonly Func<EntityId> _local;
    private readonly Func<IReadOnlyList<PartyMember>> _party;
    private readonly SummonOwnerIndex _summons;
    private readonly Func<long, long>? _askGameOwner;

    public EffectOwnerClassifier(Func<EntityId> local, Func<IReadOnlyList<PartyMember>> party, SummonOwnerIndex summons,
        Func<long, long>? askGameOwner = null)
    {
        _local = local;
        _party = party;
        _summons = summons;
        _askGameOwner = askGameOwner;
    }

    public VisibilityLayers Classify(long casterUuid)
    {
        if (casterUuid == 0) return VisibilityLayers.None;
        var owner = _summons.TopOwner(casterUuid);
        var layer = LayerOf(owner);
        for (var i = 0; layer == VisibilityLayers.None && _askGameOwner is not null && i < MaxAsks; i++)
        {
            var answer = _askGameOwner(owner);
            if (answer == 0 || answer == owner) return VisibilityLayers.None;
            _summons.Record(new EntityId(answer), new EntityId(owner));
            var next = _summons.TopOwner(answer);
            if (next == owner) return VisibilityLayers.None;   // the answer leads back to where we started: a cycle
            owner = next;
            layer = LayerOf(owner);
        }
        return layer;
    }

    private VisibilityLayers LayerOf(long ownerUuid)
    {
        var owner = new EntityId(ownerUuid);
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
