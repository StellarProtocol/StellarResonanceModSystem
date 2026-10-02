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

    [Fact]
    public void A_held_pause_reports_the_held_point_without_reading_the_game()
    {
        var r = new PosingRig();
        var t = r.Target(2);
        t.PlayAction(9021);
        t.Moment = 0.3f;
        var reads = r.Backend.Model(2).Calls.FindAll(c => c == "read").Count;
        Assert.True(r.Svc.TryGetCurrentAction(new EntityId(2), out var id, out var moment));
        Assert.Equal(9021, id);
        Assert.Equal(0.3f, moment);
        Assert.Equal(reads, r.Backend.Model(2).Calls.FindAll(c => c == "read").Count);
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
        Assert.Equal(new[] { "moment 0.40" }, r.Backend.Model(2).Calls);   // no "play": never restarted
        Assert.Equal(0.4f, t.Moment);
        t.Moment = 0.7f;   // scrub from there
        Assert.Equal(0.7f, t.Moment);
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
        Assert.Equal(new[] { "moment 0.40", "moment -1.00", "play 9206" }, r.Backend.Model(2).Calls);
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
        Assert.Equal(new[] { "moment 0.20" }, r.Backend.Model(3).Calls);
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
