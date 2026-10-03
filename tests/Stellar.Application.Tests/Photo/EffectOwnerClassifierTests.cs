using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;
namespace Stellar.Application.Tests.Photo;

public sealed class EffectOwnerClassifierTests
{
    private static long Player(long uid) => (uid << 16) | 640;
    private static long Monster(long uid) => (uid << 16) | 64;
    private static long Summon(long uid) => (uid << 16) | 1234;   // neither player nor monster marker

    private readonly SummonOwnerIndex _summons = new();
    private EntityId _local = new(Player(1));
    private readonly List<PartyMember> _party = new();

    private EffectOwnerClassifier Make() => new(() => _local, () => _party, _summons.TopOwner);

    private static PartyMember Member(long charId, bool self = false) =>
        new(charId, null, 0, 0, 0, 0, 0, default, IsOnline: true, IsSelf: self, GroupId: 0);

    [Fact] public void Zero_caster_is_never_hidden() => Assert.Equal(VisibilityLayers.None, Make().Classify(0));

    [Fact] public void Local_player_is_mine() => Assert.Equal(VisibilityLayers.EffectsMine, Make().Classify(Player(1)));

    [Fact]
    public void Party_member_is_party_and_stranger_is_others()
    {
        _party.Add(Member(1, self: true));
        _party.Add(Member(2));
        Assert.Equal(VisibilityLayers.EffectsParty, Make().Classify(Player(2)));
        Assert.Equal(VisibilityLayers.EffectsOthers, Make().Classify(Player(3)));
    }

    [Fact] public void Monster_is_monsters() => Assert.Equal(VisibilityLayers.EffectsMonsters, Make().Classify(Monster(9)));

    [Fact]
    public void Summon_resolves_to_its_top_owner()
    {
        _summons.OnCombatEvent(new CombatEvent.EntitySummonAppeared(0, new EntityId(Player(1)), new EntityId(Summon(50))));
        _summons.OnCombatEvent(new CombatEvent.EntitySummonAppeared(0, new EntityId(Summon(50)), new EntityId(Summon(51))));
        Assert.Equal(VisibilityLayers.EffectsMine, Make().Classify(Summon(51)));
    }

    [Fact] public void Unknown_summon_is_never_guessed() => Assert.Equal(VisibilityLayers.None, Make().Classify(Summon(77)));

    [Fact]
    public void Unknown_local_player_never_classifies_as_mine()
    {
        _local = EntityId.None;
        Assert.Equal(VisibilityLayers.EffectsOthers, Make().Classify(Player(1)));
    }

    [Fact]
    public void Summon_owner_cycle_terminates()
    {
        _summons.OnCombatEvent(new CombatEvent.EntitySummonAppeared(0, new EntityId(Summon(60)), new EntityId(Summon(61))));
        _summons.OnCombatEvent(new CombatEvent.EntitySummonAppeared(0, new EntityId(Summon(61)), new EntityId(Summon(60))));
        Assert.Equal(VisibilityLayers.None, Make().Classify(Summon(60)));
    }
}
