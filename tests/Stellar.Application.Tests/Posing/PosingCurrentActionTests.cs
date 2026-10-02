using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Posing;

// Owner bug 2026-10-02 (MAIN): selecting a person who is ALREADY doing an emote showed "Pick a pose" + Moment 0 %.
// IPosing.TryGetCurrentAction reads what they are doing (posed copy first, else the live person), and holding a moment
// before anything was played adopts that action WITHOUT re-playing it. Pinned regression tests — never weaken.
public sealed class PosingCurrentActionTests
{
    private static PoseActionReading Running(int id, float moment) => new(id, moment);

    [Fact]
    public void An_idle_person_reports_nothing()
    {
        var r = new PosingRig();
        Assert.False(r.Svc.TryGetCurrentAction(new EntityId(2), out var id, out var moment));
        Assert.Equal(0, id);
        Assert.Equal(-1f, moment);
    }

    [Fact]
    public void A_person_already_doing_an_emote_is_detected_without_selecting_or_opening_them()
    {
        var r = new PosingRig();
        r.Backend.LiveActions[2] = Running(9206, 0.4f);
        Assert.True(r.Svc.TryGetCurrentAction(new EntityId(2), out var id, out var moment));
        Assert.Equal(9206, id);
        Assert.Equal(0.4f, moment);
        Assert.Empty(r.Backend.Opened);
    }

    [Fact]
    public void A_selected_but_unposed_person_is_read_live()
    {
        var r = new PosingRig();
        r.Target(1);
        r.Backend.LiveActions[1] = Running(9020, 0.1f);
        Assert.True(r.Svc.TryGetCurrentAction(new EntityId(1), out var id, out _));
        Assert.Equal(9020, id);
        Assert.Empty(r.Backend.Opened);
    }

    [Fact]
    public void Nothing_is_read_while_posing_is_unavailable()
    {
        var r = new PosingRig(acquire: false);
        r.Backend.LiveActions[2] = Running(9206, 0.4f);
        Assert.False(r.Svc.TryGetCurrentAction(new EntityId(2), out var id, out _));
        Assert.Equal(0, id);
        Assert.Equal(0, r.Backend.LiveActionReads);
    }

    [Fact]
    public void A_throwing_read_is_nothing_and_never_warns()
    {
        var r = new PosingRig();
        r.Backend.ThrowOnReadAction = true;
        Assert.False(r.Svc.TryGetCurrentAction(new EntityId(2), out _, out _));
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void The_posed_copy_is_preferred_over_the_live_person()
    {
        var r = new PosingRig();
        var t = r.Target(2);
        t.Yaw = 30f;   // opens the copy (Ready); it inherited the person's action
        var copy = r.Backend.Model(2);
        copy.RunningId = 9206;
        copy.Live = 0.5f;
        r.Backend.LiveActions[2] = Running(9020, 0.9f);   // the hidden real player
        Assert.True(r.Svc.TryGetCurrentAction(new EntityId(2), out var id, out var moment));
        Assert.Equal(9206, id);
        Assert.Equal(0.5f, moment);
    }

    // Fix round 1 (3): game truth whenever a model exists, paused or not — never the intent's claim.
    [Fact]
    public void A_held_pause_reports_what_the_model_really_holds()
    {
        var r = new PosingRig();
        var t = r.Target(2);
        t.PlayAction(9021);
        t.Moment = 0.3f;
        var copy = r.Backend.Model(2);
        copy.RunningId = 9022;   // the game says something else runs: the game wins over the intent (9021)
        Assert.True(r.Svc.TryGetCurrentAction(new EntityId(2), out var id, out var moment));
        Assert.Equal(9022, id);
        Assert.Equal(0.3f, moment);   // the model's own held point
        Assert.Contains("read", copy.Calls);
        copy.Live = -1f;   // the model lost the action: no hold is claimed
        Assert.False(r.Svc.TryGetCurrentAction(new EntityId(2), out _, out _));
    }

    [Fact]
    public void A_held_pause_on_a_still_loading_npc_reports_the_intent()
    {
        var r = new PosingRig();
        var npc = r.Target(3);
        npc.PlayAction(9300);
        npc.Moment = 0.45f;
        Assert.Equal(PoseTargetState.Loading, npc.State);
        Assert.True(r.Svc.TryGetCurrentAction(new EntityId(3), out var id, out var moment));
        Assert.Equal(9300, id);
        Assert.Equal(0.45f, moment);
    }

    [Fact]
    public void A_released_person_reports_nothing()
    {
        var r = new PosingRig();
        var t = (PoseTarget)r.Target(2);
        t.PlayAction(9020);
        r.Backend.Remove(2);   // despawn: released
        Assert.Equal(PoseTargetState.Released, t.State);
        Assert.Equal(PoseActionReading.None, t.CurrentAction());
        Assert.False(r.Svc.TryGetCurrentAction(new EntityId(2), out _, out _));
    }

    [Fact]
    public void Camera_release_stops_detection()
    {
        var r = new PosingRig();
        r.Backend.LiveActions[2] = Running(9206, 0.4f);
        r.Control!.Dispose();
        Assert.False(r.Svc.TryGetCurrentAction(new EntityId(2), out _, out _));
    }

    [Fact]
    public void Pausing_a_detected_action_holds_it_where_it_is_without_replaying_it()
    {
        var r = new PosingRig();
        r.Backend.LiveActions[2] = Running(9206, 0.4f);
        var t = r.Target(2);
        t.Moment = 0.4f;
        Assert.Equal(PoseTargetState.Ready, t.State);
        Assert.Equal(new[] { "read", "moment 0.40 adopt" }, r.Backend.Model(2).Calls);   // no "play": never restarted
        Assert.Equal(0.4f, t.Moment);
        t.Moment = 0.7f;   // scrub from there
        Assert.Equal(0.7f, t.Moment);
        r.Backend.Model(2).RunningId = 9206;   // the copy runs the person's inherited action
        Assert.True(r.Svc.TryGetCurrentAction(new EntityId(2), out var id, out var moment));
        Assert.Equal(9206, id);
        Assert.Equal(0.7f, moment);
    }

    [Fact]
    public void Restarting_a_detected_action_plays_it_from_the_start()
    {
        var r = new PosingRig();
        r.Backend.LiveActions[2] = Running(9206, 0.4f);
        var t = r.Target(2);
        t.Moment = 0.4f;
        Assert.Equal(PoseResult.Applied, t.PlayAction(9206));
        Assert.Equal(new[] { "read", "moment 0.40 adopt", "moment -1.00", "play 9206" }, r.Backend.Model(2).Calls);
    }

    [Fact]
    public void Playing_a_detected_action_on_again_does_not_open_a_copy()
    {
        var r = new PosingRig();
        r.Backend.LiveActions[2] = Running(9206, 0.4f);
        var t = r.Target(2);
        t.Moment = -1f;   // "play": it already plays
        Assert.Empty(r.Backend.Opened);
        Assert.Equal(-1f, t.Moment);
    }

    [Fact]
    public void Holding_with_nothing_running_still_does_nothing()
    {
        var r = new PosingRig();
        var t = r.Target(2);
        t.Moment = 0.5f;
        Assert.Empty(r.Backend.Opened);
        Assert.Equal(-1f, t.Moment);
    }

    [Fact]
    public void A_held_detected_action_on_a_loading_npc_is_held_not_played_when_it_arrives()
    {
        var r = new PosingRig();
        r.Backend.LiveActions[3] = Running(9300, 0.2f);
        var npc = r.Target(3);
        npc.Moment = 0.2f;
        Assert.Equal(PoseTargetState.Loading, npc.State);
        r.Backend.PendingLoads[0](true);
        Assert.Equal(new[] { "read", "moment 0.20 adopt" }, r.Backend.Model(3).Calls);
    }

    // Fix round 1 (2): an opened model that runs no action (a copy that came up idle, an NPC stand-in) drops the
    // adopted action — nothing claims a hold the model lacks, and the panel is told.
    [Fact]
    public void An_adopted_action_the_opened_model_lacks_is_dropped_and_changed_is_raised()
    {
        var r = new PosingRig();
        r.Backend.LiveActions[3] = Running(9300, 0.2f);
        var npc = r.Target(3);
        npc.Moment = 0.2f;
        r.Backend.LiveActions.Remove(3);
        var changed = 0;
        r.Svc.Changed += () => changed++;
        r.Backend.Model(3).Live = -1f;   // the stand-in is idle
        r.Backend.PendingLoads[0](true);
        Assert.Equal(PoseTargetState.Ready, npc.State);
        Assert.Equal(new[] { "read" }, r.Backend.Model(3).Calls);   // no play, no hold
        Assert.Equal(-1f, npc.Moment);
        Assert.Equal(1, changed);
        Assert.False(r.Svc.TryGetCurrentAction(new EntityId(3), out _, out _));
        npc.Reset();
        Assert.Equal(PoseTouches.None, r.Backend.Model(3).Closed);   // nothing to undo
    }

    // Fix round 1 (2): only an explicitly adopted intent may hold what the model inherited; a played-but-refused
    // action's pause never freezes whatever the copy came up with.
    [Fact]
    public void A_hold_after_a_refused_play_is_not_an_adoption()
    {
        var r = new PosingRig();
        r.Backend.RefuseActions = true;
        var t = r.Target(2);
        Assert.Equal(PoseResult.Refused, t.PlayAction(9020));
        t.Moment = 0.5f;
        Assert.Contains("moment 0.50", r.Backend.Model(2).Calls);
        Assert.DoesNotContain("moment 0.50 adopt", r.Backend.Model(2).Calls);
    }

    [Fact]
    public void A_copy_opened_first_then_paused_adopts_only_when_the_copy_really_plays()
    {
        var r = new PosingRig();
        r.Backend.LiveActions[2] = Running(9206, 0.4f);   // the hidden real player
        var t = r.Target(2);
        t.Yaw = 10f;   // opens the copy
        var copy = r.Backend.Model(2);
        copy.Live = -1f;   // the copy came up idle
        t.Moment = 0.4f;
        Assert.DoesNotContain(copy.Calls, c => c.StartsWith("moment"));
        copy.Live = 0.4f;   // now the copy plays its inherited action
        t.Moment = 0.4f;
        Assert.Contains("moment 0.40 adopt", copy.Calls);
    }

    [Fact]
    public void Reset_after_holding_a_detected_action_forgets_it()
    {
        var r = new PosingRig();
        r.Backend.LiveActions[2] = Running(9206, 0.4f);
        var t = r.Target(2);
        t.Moment = 0.4f;
        var copy = r.Backend.Model(2);
        t.Reset();
        Assert.Equal(PoseTouches.Action, copy.Closed);
        Assert.Equal(PoseTargetState.Idle, t.State);
        r.Backend.LiveActions[2] = Running(9206, 0.6f);   // the real person, still dancing
        Assert.True(r.Svc.TryGetCurrentAction(new EntityId(2), out _, out var moment));
        Assert.Equal(0.6f, moment);
    }

    [Fact]
    public void The_plugin_facade_forwards_the_read()
    {
        var r = new PosingRig();
        r.Backend.LiveActions[2] = Running(9206, 0.4f);
        IPosing posing = new PluginPosing(r.Svc, new object());
        Assert.True(posing.TryGetCurrentAction(new EntityId(2), out var id, out _));
        Assert.Equal(9206, id);
    }
}
