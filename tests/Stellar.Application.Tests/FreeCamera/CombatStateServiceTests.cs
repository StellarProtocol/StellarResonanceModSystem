using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Spec § 7: event-driven, never polled; attr 104 for the local player OR the game's local combat setters.
public sealed class CombatStateServiceTests
{
    private sealed class FakeFlags : ICombatFlagSource
    {
        public bool? Seed;
        public int Hooks;
        public event Action<CombatFlagKind, bool>? Changed;
        public bool? ReadLocalInCombat() => Seed;
        public void EnsureHooks() => Hooks++;
        public void Raise(CombatFlagKind k, bool on) => Changed?.Invoke(k, on);
    }

    private static readonly EntityId Self = new(42);

    private static (CombatStateService Svc, StubCombat Combat, FakeFlags Flags, List<bool> Changes) Make()
    {
        var combat = new StubCombat { LocalEntityId = Self };
        var flags = new FakeFlags();
        var svc = new CombatStateService(combat, combat, flags, a => a());   // post inline
        var changes = new List<bool>();
        svc.Changed += changes.Add;
        return (svc, combat, flags, changes);
    }

    private static CombatEvent Attrs(EntityId id, params (int Id, long Value)[] attrs)
    {
        var list = new List<AttrValue>();
        foreach (var (a, v) in attrs) list.Add(new AttrValue(a, v));
        return new CombatEvent.EntityAttributesChanged(0, id, list);
    }

    [Fact]
    public void Attr_104_above_zero_for_the_local_player_means_in_combat()
    {
        var (svc, combat, _, changes) = Make();
        combat.Raise(Attrs(Self, (11, 5), (104, 1)));
        Assert.True(svc.LocalPlayerInCombat);
        combat.Raise(Attrs(Self, (104, 0)));
        Assert.False(svc.LocalPlayerInCombat);
        Assert.Equal(new[] { true, false }, changes);
    }

    [Fact]
    public void Other_entities_and_other_attrs_are_ignored()
    {
        var (svc, combat, _, changes) = Make();
        combat.Raise(Attrs(new EntityId(7), (104, 1)));
        combat.Raise(Attrs(Self, (114, 999)));
        Assert.False(svc.LocalPlayerInCombat);
        Assert.Empty(changes);
    }

    [Fact]
    public void Local_setters_are_a_fallback_and_all_sources_are_ORed()
    {
        var (svc, combat, flags, changes) = Make();
        flags.Raise(CombatFlagKind.LocalCombatData, true);
        Assert.True(svc.LocalPlayerInCombat);
        flags.Raise(CombatFlagKind.InBattleShow, true);
        flags.Raise(CombatFlagKind.LocalCombatData, false);
        Assert.True(svc.LocalPlayerInCombat);          // battle-show still on
        flags.Raise(CombatFlagKind.InBattleShow, false);
        Assert.False(svc.LocalPlayerInCombat);
        Assert.Equal(new[] { true, false }, changes);
    }

    [Fact]
    public void Reseed_takes_one_read_and_clears_the_local_sources()
    {
        var (svc, _, flags, _) = Make();
        flags.Raise(CombatFlagKind.InBattleShow, true);
        flags.Seed = false;
        svc.Reseed();
        Assert.False(svc.LocalPlayerInCombat);
        flags.Seed = true;
        svc.Reseed();
        Assert.True(svc.LocalPlayerInCombat);
    }

    [Fact]
    public void Off_thread_attr_events_are_posted()
    {
        var combat = new StubCombat { LocalEntityId = Self };
        var posted = new List<Action>();
        var svc = new CombatStateService(combat, combat, new FakeFlags(), posted.Add);
        combat.Raise(Attrs(Self, (104, 1)));
        Assert.False(svc.LocalPlayerInCombat);         // nothing changes until the main thread runs the post
        posted[0]();
        Assert.True(svc.LocalPlayerInCombat);
    }

    [Fact]
    public void Off_thread_flag_events_are_posted()
    {
        var combat = new StubCombat { LocalEntityId = Self };
        var flags = new FakeFlags();
        var posted = new List<Action>();
        var svc = new CombatStateService(combat, combat, flags, posted.Add);
        flags.Raise(CombatFlagKind.LocalCombatData, true);
        Assert.False(svc.LocalPlayerInCombat);         // nothing changes until the main thread runs the post
        posted[0]();
        Assert.True(svc.LocalPlayerInCombat);
    }

    [Fact]
    public void Subscribing_installs_the_fallback_hooks()
    {
        var (_, _, flags, _) = Make();
        Assert.True(flags.Hooks >= 1);
    }

    [Fact]
    public void Facade_release_drops_its_handlers()
    {
        var combat = new StubCombat { LocalEntityId = Self };
        var svc = new CombatStateService(combat, combat, new FakeFlags(), a => a());
        var p = new PluginCombatState(svc);
        var calls = 0;
        p.Changed += _ => calls++;
        p.ReleaseAll();
        combat.Raise(Attrs(Self, (104, 1)));
        Assert.Equal(0, calls);
    }
}
