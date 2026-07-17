using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// TDD guard for the idle-entity sweep (FPS cache-leak fix, Task 3). Mobs touched
/// only by damage packets never get an AOI-disappear, and on a long single-map
/// session <see cref="CombatEntityTracker.Reset"/> never fires either, so their
/// vitals/skills/buffs would otherwise accumulate for the process lifetime — see
/// that method's doc comment for the hole this sweep closes.
///
/// <para>Clock consistency: every write path (<c>UpdateEntityVitals</c>,
/// <c>AccumulateDps</c>/<c>AccumulateHps</c>, <c>SetEntityAttribute</c>,
/// <c>ApplyBuffEvents</c>) touches with the REAL <c>Environment.TickCount64</c>,
/// not a test-controlled value. Because <see cref="CombatEntityTracker.Touch"/>
/// is a last-write-wins upsert (not a max), every test below pins a deterministic
/// last-touched value with an explicit trailing <c>Touch(id, nowMs)</c> call after
/// seeding state through those real-clock paths, so results never depend on the
/// machine's actual uptime.</para>
/// </summary>
public sealed class EntityIdleSweepTests
{
    // (1_000_001 & 0xFFFF) = 16961 != 640 -> non-player mob.
    private static readonly EntityId Mob = new(1_000_001);
    // PartyMember.EntityId shape: (CharId << 16) | 640 -> low 16 bits == 640 -> IsPlayer.
    // Roster members are exempted from the sweep by construction (EntityId.IsPlayer),
    // not by any roster-membership lookup.
    private static readonly EntityId RosterPlayer = new((123456L << 16) | 640);

    private static CombatService MakeService(out CombatEntityTracker tracker)
    {
        tracker = new CombatEntityTracker();
        return new CombatService(new StubLog(), tracker, new SocialDataCache(), new StubSocialRefreshRequester());
    }

    [Fact]
    public void Idle_mob_is_swept_after_ttl()
    {
        var svc = MakeService(out var tracker);

        svc.UpdateEntityVitals(Mob, hp: 100, maxHp: 100);
        tracker.Touch(Mob, nowMs: 0);

        svc.SweepIdleEntities(CombatService.IdleEntityTtlMs + 1);

        Assert.False(tracker.GetVitals(Mob).IsKnown);
    }

    [Fact]
    public void Retouched_mob_survives()
    {
        var svc = MakeService(out var tracker);

        svc.UpdateEntityVitals(Mob, hp: 100, maxHp: 100);
        tracker.Touch(Mob, nowMs: 0);
        tracker.Touch(Mob, nowMs: CombatService.IdleEntityTtlMs);

        svc.SweepIdleEntities(CombatService.IdleEntityTtlMs + 1);

        Assert.True(tracker.GetVitals(Mob).IsKnown);
    }

    [Fact]
    public void Roster_player_is_never_swept()
    {
        var svc = MakeService(out var tracker);

        svc.UpdateEntityVitals(RosterPlayer, hp: 100, maxHp: 100);
        tracker.Touch(RosterPlayer, nowMs: 0);   // no-op: EntityId.IsPlayer short-circuits Touch

        // Sweep at 10x the TTL — RosterPlayer must never have entered _lastTouchedMs at all,
        // so it is never a CollectIdle candidate regardless of how long it goes untouched.
        svc.SweepIdleEntities(CombatService.IdleEntityTtlMs * 10);

        Assert.True(tracker.GetVitals(RosterPlayer).IsKnown);
    }

    [Fact]
    public void Sweep_clears_mob_skills_and_buffs()
    {
        var svc = MakeService(out var tracker);

        tracker.UpdateEntitySkillLevels(Mob, new[] { new SkillLevel(SkillId: 1, Level: 2, Tier: 3) });

        var buff = new ActiveBuff(BuffUuid: 1, BaseId: 9001, Level: 1, FirerId: EntityId.None,
                                   Stacks: 1, Layer: 1, CreateTimeMs: 0, DurationMs: 5000);
        svc.ApplyBuffEvents(Mob, new[] { buff }, System.Array.Empty<int>(), timestampMs: 0);

        // Pin a deterministic last-touched value, overriding whatever real-clock touch
        // ApplyBuffEvents just made (last-write-wins — see class doc comment).
        tracker.Touch(Mob, nowMs: 0);

        svc.SweepIdleEntities(CombatService.IdleEntityTtlMs + 1);

        Assert.Empty(tracker.GetSkillLevels(Mob));
        Assert.Empty(svc.BuffsFor(Mob));
    }

    // --- Additional coverage: the buff-ingest Touch is spec'd as mandatory (not optional) —
    // an entity kept alive only by buff refreshes (no damage/vitals writes at all) must not be
    // swept. This cannot be pinned with a synthetic Touch() the way the tests above are (that
    // would defeat the point), so it anchors sweep time to the real clock relative to when the
    // buff event fired, without asserting an exact TickCount64 value.

    [Fact]
    public void Buff_refresh_alone_keeps_an_otherwise_idle_mob_from_being_swept()
    {
        var svc = MakeService(out var tracker);

        // Simulate a damage-driven touch from long ago (as if the last hit predates the TTL).
        svc.UpdateEntityVitals(Mob, hp: 100, maxHp: 100);
        tracker.Touch(Mob, nowMs: 0);

        // The entity's ONLY subsequent activity is a buff refresh, timestamped by the real clock.
        var refreshedAtRealMs = System.Environment.TickCount64;
        var buff = new ActiveBuff(BuffUuid: 1, BaseId: 9001, Level: 1, FirerId: EntityId.None,
                                   Stacks: 1, Layer: 1, CreateTimeMs: 0, DurationMs: 5000);
        svc.ApplyBuffEvents(Mob, new[] { buff }, System.Array.Empty<int>(), timestampMs: 0);

        // Sweep just short of a full TTL past the buff refresh. If ApplyBuffEvents had NOT
        // touched the entity, its last-touched value would still be the synthetic 0 from
        // above, which is always >= TTL behind this real-clock sweep point on any machine
        // that has been up longer than the TTL — so this would incorrectly evict without the
        // buff-ingest Touch wired.
        svc.SweepIdleEntities(refreshedAtRealMs + CombatService.IdleEntityTtlMs - 1);

        Assert.True(tracker.GetVitals(Mob).IsKnown);
    }
}
