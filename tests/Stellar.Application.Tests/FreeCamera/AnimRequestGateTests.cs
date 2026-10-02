using System;
using System.Reflection;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Owner report 2026-10-02 (watching the TEST window during run 10): "i can see someone animated movement when freeze". Run 11
// measured why: at Time.timeScale = 0 the ECS animator's clock is frozen (0 animation events over a 20 s pause on every entity
// in range), but the game's logic keeps REQUESTING states for movers (a walking monster: 9 PlayBaseState, 5 PlayUpperState,
// 5 playManualClip in 20 s paused) and each request switches its pose. AnimRequestGate holds them: only tracked controllers
// (the press's entities — never the local player, their mount, a posing copy or an NPC model), the latest per layer, replayed
// in arrival order on unfreeze; a leaving (pooled) controller is forgotten. Do not weaken.
public sealed class AnimRequestGateTests
{
    private const nint Walker = 0x100, Copy = 0x200, Other = 0x300;
    private static readonly MethodBase Base = typeof(AnimRequestGateTests).GetMethod(nameof(PlayBase), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodBase Upper = typeof(AnimRequestGateTests).GetMethod(nameof(PlayUpper), BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void freeze_anim_requests_of_a_tracked_mover_are_held_latest_per_layer_and_replayed_in_order()
    {
        var g = Armed();
        Assert.False(g.Decide(Req(Walker, AnimRequestGate.Layer.Base, Base, "run")));
        Assert.False(g.Decide(Req(Walker, AnimRequestGate.Layer.Upper, Upper, "wave")));
        Assert.False(g.Decide(Req(Walker, AnimRequestGate.Layer.Base, Base, "idle")));   // newer base request replaces "run"
        Assert.Equal(2, g.Kept);
        Assert.Equal(3, g.Deferred);

        var replay = g.TakeReplay(SameController);
        Assert.Equal(new[] { "wave", "idle" }, replay.ConvertAll(p => (string)p.Args[0]!));   // arrival order of the kept ones
        Assert.All(replay, p => Assert.Equal(7L, p.Uuid));
        Assert.False(g.Armed);
        Assert.Equal(0, g.Kept);
        Assert.True(g.Decide(Req(Walker, AnimRequestGate.Layer.Base, Base, "after")));   // unfrozen: the game plays it
    }

    [Fact]
    public void freeze_anim_untracked_controllers_offthread_calls_and_replays_always_run()
    {
        var g = Armed();
        Assert.True(g.Decide(Req(Copy, AnimRequestGate.Layer.Base, Base, "pose")));            // a posing copy / self: not tracked
        Assert.True(g.Decide(Req(Walker, AnimRequestGate.Layer.Base, Base, "x") with { OffMainThread = true }));
        Assert.True(g.Decide(Req(0, AnimRequestGate.Layer.Base, Base, "x")));
        g.Replaying = true;
        Assert.True(g.Decide(Req(Walker, AnimRequestGate.Layer.Base, Base, "replayed")));     // the replay itself runs
        g.Replaying = false;
        Assert.Equal(0, g.Kept);
        Assert.True(new AnimRequestGate().Decide(Req(Walker, AnimRequestGate.Layer.Base, Base, "unarmed")));
    }

    [Fact]
    public void freeze_anim_a_leaving_entity_is_untracked_and_its_kept_requests_dropped()
    {
        var g = Armed();
        g.Track(Other, 8);
        Assert.False(g.Decide(Req(Walker, AnimRequestGate.Layer.Base, Base, "a")));
        Assert.False(g.Decide(Req(Other, AnimRequestGate.Layer.Base, Base, "b")));
        g.Untrack(7);                                                                            // RemoveEntity ran for uuid 7
        Assert.Equal(1, g.Tracked);
        Assert.True(g.Decide(Req(Walker, AnimRequestGate.Layer.Base, Base, "re-rented")));     // its pooled controller is free
        Assert.Equal(new[] { "b" }, g.TakeReplay(SameController).ConvertAll(p => (string)p.Args[0]!));
    }

    [Fact]
    public void freeze_anim_kept_arguments_are_copied_and_the_queue_is_bounded()
    {
        var g = Armed();
        var args = new object?[] { "run" };
        Assert.False(g.Decide(new AnimRequestGate.Request(Walker, "ctl", AnimRequestGate.Layer.Base, Base, args, false)));
        args[0] = "mutated by the game";
        Assert.Equal("run", g.TakeReplay(SameController)[0].Args[0]);

        var full = new AnimRequestGate();
        full.Arm();
        for (nint c = 1; c <= AnimRequestGate.Cap; c++) { full.Track(c, c); Assert.False(full.Decide(Req(c, AnimRequestGate.Layer.Base, Base, "x"))); }
        full.Track(AnimRequestGate.Cap + 1, 99);
        Assert.True(full.Decide(Req(AnimRequestGate.Cap + 1, AnimRequestGate.Layer.Base, Base, "over")));   // full: plays now
        Assert.False(full.Decide(Req(1, AnimRequestGate.Layer.Base, Base, "replace")));                      // a replace still holds
        Assert.Equal(AnimRequestGate.Cap, full.Kept);
    }

    // Review M-10 (2026-10-03): a controller is POOLED (ECSAnimController.Rent/Return) — the entity of a kept request may
    // serve another controller at unfreeze, and its old one may drive someone else by then. TakeReplay keeps a request only
    // while its entity still serves the SAME controller; the rest are dropped as stale. Do not weaken.
    [Fact]
    public void freeze_anim_replay_skips_a_request_whose_entity_serves_another_controller_now()
    {
        var g = Armed();
        g.Track(Other, 8);
        Assert.False(g.Decide(Req(Walker, AnimRequestGate.Layer.Base, Base, "walker")));
        Assert.False(g.Decide(Req(Other, AnimRequestGate.Layer.Base, Base, "other")));
        var replay = g.TakeReplay(uuid => uuid == 7 ? Copy : Other);                 // uuid 7 was re-rented Copy meanwhile
        Assert.Equal(new[] { "other" }, replay.ConvertAll(p => (string)p.Args[0]!));
        Assert.Equal(1, g.Stale);
        Assert.Empty(new AnimRequestGate().TakeReplay(_ => throw new InvalidOperationException()));   // nothing kept: no read
        var h = Armed();
        Assert.False(h.Decide(Req(Walker, AnimRequestGate.Layer.Base, Base, "gone")));
        Assert.Empty(h.TakeReplay(_ => throw new InvalidOperationException("entity freed")));   // an unreadable entity: dropped
        Assert.Equal(1, h.Stale);
    }

    // Review M-7 (2026-10-03): a press that did not know the local player (uuid unread = 0) still has the player in its entity
    // list, so tracking that list would gate the player's own controller — their movement pose stuck. Such a press tracks
    // nobody, exactly as the position hold holds nobody (FreezeTargets.MayHoldAny). Do not weaken.
    [Fact]
    public void freeze_anim_a_press_that_did_not_know_the_player_tracks_nobody()
    {
        var ids = new System.Collections.Generic.List<long> { 7, 8, 42 };
        var unknown = new FreezeLedger();
        unknown.Begin(self: 0);
        var g = new AnimRequestGate();
        g.ArmFor(unknown, ids, uuid => (nint)(uuid * 0x10));
        Assert.True(g.Armed);
        Assert.Equal(0, g.Tracked);
        Assert.True(g.Decide(Req(42 * 0x10, AnimRequestGate.Layer.Base, Base, "self")));

        var known = new FreezeLedger();
        known.Begin(self: 42);
        g.ArmFor(known, new System.Collections.Generic.List<long> { 7, 8, 9 }, uuid => uuid == 9 ? 0 : (nint)(uuid * 0x10));
        Assert.Equal(2, g.Tracked);                                                   // 9 has no live controller
        Assert.False(g.Decide(Req(7 * 0x10, AnimRequestGate.Layer.Base, Base, "walker")));
    }

    // Perf review 2026-10-03 (major 1): the typed prefixes ask Defers first and build the argument copy only for a call that
    // is really kept — Defers itself never keeps, counts or allocates. Do not weaken.
    [Fact]
    public void freeze_anim_defers_only_answers_and_keep_holds_the_latest()
    {
        var g = Armed();
        Assert.True(g.Defers(Walker, AnimRequestGate.Layer.Base, offMainThread: false));
        Assert.True(g.Defers(Walker, AnimRequestGate.Layer.Base, offMainThread: false));
        Assert.Equal(0, g.Kept);
        Assert.Equal(0, g.Deferred);
        Assert.False(g.Defers(Walker, AnimRequestGate.Layer.Base, offMainThread: true));
        Assert.False(g.Defers(Copy, AnimRequestGate.Layer.Base, offMainThread: false));
        g.Keep(Walker, "ctl", AnimRequestGate.Layer.Base, Base, new object?[] { "a" });
        g.Keep(Walker, "ctl", AnimRequestGate.Layer.Base, Base, new object?[] { "b" });
        g.Keep(Copy, "ctl", AnimRequestGate.Layer.Base, Base, new object?[] { "untracked" });   // never kept
        Assert.Equal(1, g.Kept);
        Assert.Equal(new[] { "b" }, g.TakeReplay(SameController).ConvertAll(p => (string)p.Args[0]!));
    }

    // The replay's identity check with nothing re-rented: uuid 7 → Walker, 8 → Other, any other uuid c → controller c.
    private static nint SameController(long uuid) => uuid switch { 7 => Walker, 8 => Other, _ => (nint)uuid };

    private static AnimRequestGate Armed()
    {
        var g = new AnimRequestGate();
        g.Arm();
        g.Track(Walker, 7);
        return g;
    }

    private static AnimRequestGate.Request Req(nint ctl, AnimRequestGate.Layer layer, MethodBase m, string arg) =>
        new(ctl, "ctl", layer, m, new object?[] { arg }, false);

    private static void PlayBase(string s) { }

    private static void PlayUpper(string s) { }
}
