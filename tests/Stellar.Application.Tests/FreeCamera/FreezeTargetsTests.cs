using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Photo Studio scene-stays review (2026-10-02). I1: the local player's own mount (VehicleEnt, a separate entity) was frozen
// and position-held, pinning / sliding the rider — it must be excluded like the player, at the press AND when it appears
// (or links on ride-up) mid-freeze. I2: the self-exclusion WIRING (read ids → Begin(self) → drop self) and the hold
// admission were unpinned; they live in the pure FreezeTargets and are driven here through a fake entity source. M1: a
// press that read uuid 0 learns the player at stage 2. Do not weaken.
public sealed class FreezeTargetsTests
{
    private const long Self = 42, Other = 7, Ridden = 99, Driven = 100, OthersMount = 101, Npc = 8;

    [Fact]
    public void scene_stays_own_mount_never_frozen()
    {
        var src = Town();
        var l = new FreezeLedger();
        var ids = new List<long>();
        FreezeTargets.Select(src, l, ids);

        Assert.Equal(new[] { Other, OthersMount, Npc }, ids);                // self, the ridden and the driven mount are out
        Assert.True(l.Excludes(Ridden));
        Assert.True(l.Excludes(Driven));
        Assert.False(l.SaveFactor(Ridden, 1f));                              // no stage 1 …
        Assert.False(l.AdmitSpeed(Ridden, 1f));                              // … no stage 2 …
        Assert.False(FreezeTargets.MayHold(l, Ridden, FreezeKinds.Vehicle, 1f, false));   // … no hold
        Assert.True(FreezeTargets.MayHold(l, OthersMount, FreezeKinds.Vehicle, 1f, false));

        // A mount summoned mid-freeze: refused as it appears when its driver is the local player …
        Assert.False(FreezeTargets.AdmitAppeared(src, l, 200, FreezeKinds.Vehicle, controller: Self));
        Assert.True(l.Excludes(200));
        Assert.True(FreezeTargets.AdmitAppeared(src, l, 201, FreezeKinds.Vehicle, controller: Other));
        // … and released when the ride-up links it later (the vehicle ride event's re-check).
        src.Ridden = 201;
        Assert.True(FreezeTargets.ExcludeIfOwnMount(src, l, 201, FreezeKinds.Vehicle, controller: 0));
        Assert.True(l.Excludes(201));
    }

    [Fact]
    public void scene_stays_own_mount_never_frozen_even_when_its_kind_cannot_be_read()
    {
        var src = Town();
        src.Kinds[Ridden] = -1;                                              // kind read failed: the ride link alone decides
        var l = new FreezeLedger();
        var ids = new List<long>();
        FreezeTargets.Select(src, l, ids);
        Assert.True(l.Excludes(Ridden));
        Assert.DoesNotContain(Ridden, ids);
    }

    [Fact]
    public void scene_stays_self_wiring_reads_ids_then_begins_with_self_and_drops_them()
    {
        var src = Town();
        var l = new FreezeLedger();
        l.Begin(self: 555);                                                  // a stale previous freeze
        var ids = new List<long>();
        FreezeTargets.Select(src, l, ids);

        Assert.Equal("EntityUuids", src.Calls[0]);                           // read ids first …
        Assert.Equal("PlayerUuid", src.Calls[1]);                            // … then Begin(self)
        Assert.Equal(Self, l.Self);
        Assert.False(l.Excludes(555));                                       // Begin cleared the old freeze
        Assert.DoesNotContain(Self, ids);
        Assert.False(FreezeTargets.MayHold(l, Self, FreezeKinds.Char, 0f, false));
        Assert.False(FreezeTargets.AdmitAppeared(src, l, Self, FreezeKinds.Char, 0));
    }

    [Fact]
    public void A_press_that_did_not_know_the_player_learns_them_and_their_mount_at_stage_2()
    {
        var src = Town();
        src.Player = 0;                                                      // review M1: uuid 0 at the press
        src.Ridden = 0;
        var l = new FreezeLedger();
        var ids = new List<long>();
        FreezeTargets.Select(src, l, ids);
        Assert.Contains(Self, ids);                                          // unknown: nobody excluded yet

        src.Player = Self;
        src.Ridden = Ridden;
        var released = new List<long>();
        FreezeTargets.Recheck(src, l, ids, released);
        // the caller undoes the press on all three: the player, the mount they ride, and the press's mount they DRIVE
        // (review: the press could not match its driver against an unknown player)
        Assert.Equal(new[] { Self, Ridden, Driven }, released);
        Assert.True(l.Excludes(Self));
        Assert.True(l.Excludes(Ridden));
        Assert.True(l.Excludes(Driven));
        Assert.False(l.Excludes(OthersMount));                               // someone else's mount stays frozen
        l.WithoutSelf(ids);
        Assert.DoesNotContain(Self, ids);
        Assert.DoesNotContain(Driven, ids);

        FreezeTargets.Recheck(src, l, ids, released);
        Assert.Empty(released);                                              // nothing new: no second release
    }

    [Theory]
    [InlineData(FreezeKinds.Char, 10f, false, true)]
    [InlineData(FreezeKinds.Char, 80.5f, false, false)]                     // beyond the hold radius
    [InlineData(FreezeKinds.SceneObject, 10f, false, false)]                // not a moving kind
    [InlineData(FreezeKinds.Char, 10f, true, false)]                        // already held (re-appeared)
    public void Hold_admission(int kind, float distance, bool held, bool expected)
    {
        var l = new FreezeLedger();
        l.Begin(Self);
        Assert.Equal(expected, FreezeTargets.MayHold(l, Other, kind, distance, held));
    }

    [Fact]
    public void Unknown_links_never_make_a_mount_the_players()
    {
        Assert.False(FreezeTargets.IsOwnMount(vehicle: 0, controller: 0, self: 0, ridden: 0));
        Assert.False(FreezeTargets.IsOwnMount(vehicle: 5, controller: 0, self: 0, ridden: 0));   // driver unknown, self unknown
        Assert.False(FreezeTargets.IsOwnMount(vehicle: 5, controller: 9, self: Self, ridden: 0));
        Assert.True(FreezeTargets.IsOwnMount(vehicle: 5, controller: 9, self: Self, ridden: 5));
        Assert.True(FreezeTargets.IsOwnMount(vehicle: 5, controller: Self, self: Self, ridden: 0));
    }

    private static FakeSource Town() => new()
    {
        Player = Self,
        Ridden = Ridden,
        Ids = { Other, Self, Ridden, Driven, OthersMount, Npc },
        Kinds = { [Other] = FreezeKinds.Char, [Self] = FreezeKinds.Char, [Ridden] = FreezeKinds.Vehicle, [Driven] = FreezeKinds.Vehicle,
                  [OthersMount] = FreezeKinds.Vehicle, [Npc] = FreezeKinds.Npc },
        Controllers = { [Driven] = Self, [OthersMount] = Other },
    };

    private sealed class FakeSource : IFreezeEntitySource
    {
        public long Player;
        public long Ridden;
        public readonly List<long> Ids = new();
        public readonly Dictionary<long, int> Kinds = new();
        public readonly Dictionary<long, long> Controllers = new();
        public readonly List<string> Calls = new();

        public void EntityUuids(List<long> into)
        {
            Calls.Add(nameof(EntityUuids));
            into.Clear();
            into.AddRange(Ids);
        }

        public long PlayerUuid()
        {
            Calls.Add(nameof(PlayerUuid));
            return Player;
        }

        public long RiddenVehicle() => Ridden;
        public int Kind(long uuid) => Kinds.TryGetValue(uuid, out var k) ? k : -1;
        public long VehicleController(long uuid) => Controllers.TryGetValue(uuid, out var c) ? c : 0;
    }
}
