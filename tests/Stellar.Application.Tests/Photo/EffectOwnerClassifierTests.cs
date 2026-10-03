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

    private readonly Dictionary<long, long> _gameOwners = new();
    private readonly List<long> _asked = new();

    private EffectOwnerClassifier Make() => new(() => _local, () => _party, _summons);

    private EffectOwnerClassifier MakeAsking() => new(() => _local, () => _party, _summons, AskGame);

    private long AskGame(long uuid)
    {
        _asked.Add(uuid);
        return _gameOwners.TryGetValue(uuid, out var owner) ? owner : 0;
    }

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

    [Fact]
    public void Unannounced_pet_asks_the_game_and_resolves_to_local()
    {
        _gameOwners[Summon(80)] = Player(1);
        Assert.Equal(VisibilityLayers.EffectsMine, MakeAsking().Classify(Summon(80)));
    }

    [Fact]
    public void Game_answer_zero_stays_none()
    {
        Assert.Equal(VisibilityLayers.None, MakeAsking().Classify(Summon(81)));
        Assert.Equal(new[] { Summon(81) }, _asked);
    }

    [Fact]
    public void Game_answer_that_is_itself_a_summon_resolves_through_the_index()
    {
        _party.Add(Member(1, self: true));
        _party.Add(Member(2));
        _summons.Record(new EntityId(Player(2)), new EntityId(Summon(90)));
        _gameOwners[Summon(91)] = Summon(90);
        Assert.Equal(VisibilityLayers.EffectsParty, MakeAsking().Classify(Summon(91)));
    }

    [Fact]
    public void Game_answer_that_is_an_unindexed_summon_is_asked_again_bounded()
    {
        _gameOwners[Summon(92)] = Summon(93);
        _gameOwners[Summon(93)] = Player(1);
        Assert.Equal(VisibilityLayers.EffectsMine, MakeAsking().Classify(Summon(92)));
    }

    [Fact]
    public void Game_answer_chain_that_never_resolves_terminates()
    {
        _gameOwners[Summon(94)] = Summon(95);
        _gameOwners[Summon(95)] = Summon(94);
        Assert.Equal(VisibilityLayers.None, MakeAsking().Classify(Summon(94)));
        Assert.True(_asked.Count <= 3);   // MaxAsks
        // The game-reported cycle was never written: only 94 → 95 is stored, so both resolve to 95 (a stored
        // 95 → 94 back-link would make the bounded walk from 94 land on 94 after its even hop count).
        Assert.Equal(Summon(95), _summons.TopOwner(Summon(95)));
        Assert.Equal(Summon(95), _summons.TopOwner(Summon(94)));
    }

    [Fact]
    public void Game_answer_equal_to_caster_is_ignored()
    {
        _gameOwners[Summon(96)] = Summon(96);
        Assert.Equal(VisibilityLayers.None, MakeAsking().Classify(Summon(96)));
    }

    [Fact]
    public void Fallback_is_not_called_for_player_or_monster_casters()
    {
        var c = MakeAsking();
        c.Classify(Player(1));
        c.Classify(Player(7));
        c.Classify(Monster(3));
        Assert.Empty(_asked);
    }

    [Fact]
    public void Fallback_is_not_called_for_an_indexed_summon()
    {
        _summons.Record(new EntityId(Player(1)), new EntityId(Summon(97)));
        Assert.Equal(VisibilityLayers.EffectsMine, MakeAsking().Classify(Summon(97)));
        Assert.Empty(_asked);
    }

    [Fact]
    public void Repeated_classification_hits_the_index_not_the_fallback()
    {
        _gameOwners[Summon(98)] = Player(1);
        var c = MakeAsking();
        Assert.Equal(VisibilityLayers.EffectsMine, c.Classify(Summon(98)));
        Assert.Equal(VisibilityLayers.EffectsMine, c.Classify(Summon(98)));
        Assert.Single(_asked);
        Assert.Equal(Player(1), _summons.TopOwner(Summon(98)));
    }

    [Fact]
    public void Fallback_is_not_called_for_a_zero_caster()
    {
        Assert.Equal(VisibilityLayers.None, MakeAsking().Classify(0));
        Assert.Empty(_asked);
    }
}
