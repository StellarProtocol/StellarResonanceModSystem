using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// A1 of the 2026-07-17 sync spec: a MaxHp-only observation must NOT read as dead.
/// HasHpObservation flips only when a real hp value (>= 0, including 0 = dead) lands;
/// IsKnown semantics are unchanged for existing consumers.
/// </summary>
public sealed class VitalsObservationTests
{
    private static readonly EntityId Id = new(0x0000_0001_0000_0280L);

    [Fact]
    public void MaxHpOnly_observation_is_known_but_has_no_hp_observation()
    {
        var t = new CombatEntityTracker();
        t.UpdateEntityVitals(Id, hp: -1, maxHp: 350_000);
        var v = t.GetVitals(Id);
        Assert.True(v.IsKnown);
        Assert.False(v.HasHpObservation);
        Assert.Equal(0L, v.Hp);
        Assert.Equal(350_000L, v.MaxHp);
    }

    [Fact]
    public void Zero_hp_observation_sets_the_flag()
    {
        // hp: 0 is a REAL observation (the entity is dead) — the flag must flip.
        var t = new CombatEntityTracker();
        t.UpdateEntityVitals(Id, hp: 0, maxHp: 350_000);
        var v = t.GetVitals(Id);
        Assert.True(v.HasHpObservation);
        Assert.Equal(0L, v.Hp);
    }

    [Fact]
    public void Flag_sticks_across_subsequent_maxhp_only_updates()
    {
        var t = new CombatEntityTracker();
        t.UpdateEntityVitals(Id, hp: 200_000, maxHp: 350_000);
        t.UpdateEntityVitals(Id, hp: -1, maxHp: 360_000);   // -1 sentinel: no hp this tick
        var v = t.GetVitals(Id);
        Assert.True(v.HasHpObservation);
        Assert.Equal(200_000L, v.Hp);
        Assert.Equal(360_000L, v.MaxHp);
    }

    [Fact]
    public void Unknown_sentinel_carries_no_observation()
        => Assert.False(EntityVitals.Unknown.HasHpObservation);

    [Fact]
    public void Disappear_resets_the_flag_with_the_rest()
    {
        var t = new CombatEntityTracker();
        var mob = new EntityId(0x0000_0001_0000_0040L);
        t.UpdateEntityVitals(mob, hp: 100, maxHp: 100);
        t.OnEntityDisappeared(mob);
        Assert.False(t.GetVitals(mob).HasHpObservation);
    }
}
