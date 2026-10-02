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

        var replay = g.TakeReplay();
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
        Assert.Equal(new[] { "b" }, g.TakeReplay().ConvertAll(p => (string)p.Args[0]!));
    }

    [Fact]
    public void freeze_anim_kept_arguments_are_copied_and_the_queue_is_bounded()
    {
        var g = Armed();
        var args = new object?[] { "run" };
        Assert.False(g.Decide(new AnimRequestGate.Request(Walker, "ctl", AnimRequestGate.Layer.Base, Base, args, false)));
        args[0] = "mutated by the game";
        Assert.Equal("run", g.TakeReplay()[0].Args[0]);

        var full = new AnimRequestGate();
        full.Arm();
        for (nint c = 1; c <= AnimRequestGate.Cap; c++) { full.Track(c, c); Assert.False(full.Decide(Req(c, AnimRequestGate.Layer.Base, Base, "x"))); }
        full.Track(AnimRequestGate.Cap + 1, 99);
        Assert.True(full.Decide(Req(AnimRequestGate.Cap + 1, AnimRequestGate.Layer.Base, Base, "over")));   // full: plays now
        Assert.False(full.Decide(Req(1, AnimRequestGate.Layer.Base, Base, "replace")));                      // a replace still holds
        Assert.Equal(AnimRequestGate.Cap, full.Kept);
    }

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
