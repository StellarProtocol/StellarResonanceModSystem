using System;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Xunit;

namespace Stellar.Application.Tests.Posing;

// Spec 2026-10-02 §§ 4.1–4.5: a person's copy/model is made once, on the first control (never on selection); every
// free-camera release reason resets every touched person; a despawn drops only that person; NPC controls made while the
// model loads are applied in order when it arrives; a reset undoes only what was touched; unfreeze never resumes a pause.
public sealed class PosingServiceTests
{
    [Fact]
    public void Select_needs_a_held_free_camera_and_a_person()
    {
        var r = new PosingRig(acquire: false);
        Assert.False(r.Svc.IsAvailable);
        Assert.Null(r.Svc.Select(new EntityId(2)));
        r.Acquire();
        Assert.True(r.Svc.IsAvailable);
        Assert.NotNull(r.Svc.Select(new EntityId(2)));
        Assert.Null(r.Svc.Select(new EntityId(99)));   // not a player or an NPC
        Assert.Null(r.Svc.Select(EntityId.None));
    }

    [Fact]
    public void Selecting_opens_nothing_and_the_first_control_opens_once()
    {
        var r = new PosingRig();
        var t = r.Target(2);
        Assert.Same(t, r.Target(2));
        Assert.Empty(r.Backend.Opened);
        Assert.Equal(PoseTargetState.Idle, t.State);
        t.Yaw = 30f;
        Assert.Single(r.Backend.Opened);
        Assert.Equal(PoseTargetState.Ready, t.State);
        Assert.Equal(PoseResult.Applied, t.PlayAction(9020));
        Assert.Single(r.Backend.Opened);
        Assert.Equal(new[] { "yaw 30", "play 9020" }, r.Backend.Model(2).Calls);
    }

    [Theory]
    [InlineData(CameraReleaseReason.Disposed)]
    [InlineData(CameraReleaseReason.SceneChanged)]
    [InlineData(CameraReleaseReason.Cutscene)]
    [InlineData(CameraReleaseReason.GamePhotoMode)]
    [InlineData(CameraReleaseReason.Disconnected)]
    [InlineData(CameraReleaseReason.PluginUnloaded)]
    [InlineData(CameraReleaseReason.Error)]
    public void Every_free_camera_release_resets_every_touched_person(CameraReleaseReason reason)
    {
        var r = new PosingRig();
        var self = r.Target(1);
        var copy = r.Target(2);
        var untouched = r.Target(3);
        self.PlayAction(9020);
        self.Yaw = 15f;
        copy.SetLook(LookPart.Head, LookMode.Lens, false);
        Release(r, reason);
        Assert.Equal(PoseTouches.Action | PoseTouches.Yaw, r.Backend.Model(1).Closed);
        Assert.Equal(PoseTouches.Head, r.Backend.Model(2).Closed);
        Assert.Equal(2, r.Backend.Opened.Count);   // the untouched NPC never got a model
        Assert.All(new[] { self, copy, untouched }, t => Assert.Equal(PoseTargetState.Released, t.State));
        Assert.False(r.Svc.HasTargets);
        r.Acquire();
        Assert.NotSame(copy, r.Target(2));   // a new free camera starts clean
    }

    private static void Release(PosingRig r, CameraReleaseReason reason)
    {
        switch (reason)
        {
            case CameraReleaseReason.Disposed: r.Control!.Dispose(); break;
            case CameraReleaseReason.PluginUnloaded: r.Camera.ReleaseOwner(r.CameraOwner); break;
            case CameraReleaseReason.Error:
                r.Control!.Frame += _ => throw new InvalidOperationException("plugin frame handler");
                r.CameraBackend.RaiseFrame(0.016f);
                break;
            default: r.Camera.ReleaseAll(reason); break;
        }
    }

    [Fact]
    public void A_despawn_drops_only_that_person()
    {
        var r = new PosingRig();
        var copy = r.Target(2);
        var self = r.Target(1);
        copy.Yaw = 10f;
        self.Yaw = 5f;
        r.Backend.Remove(2);
        Assert.Equal(PoseTargetState.Released, copy.State);
        Assert.Equal(PoseTouches.Yaw, r.Backend.Model(2).Closed);
        Assert.Equal(PoseTargetState.Ready, self.State);
        Assert.Null(r.Backend.Model(1).Closed);
        r.Backend.Remove(77);   // not posed: nothing happens
        Assert.True(r.Svc.HasTargets);
    }

    [Fact]
    public void Npc_controls_made_while_the_model_loads_are_applied_in_order_when_it_arrives()
    {
        var r = new PosingRig();
        var npc = r.Target(3);
        Assert.Equal(PoseResult.Loading, npc.PlayAction(9020));
        Assert.Equal(PoseTargetState.Loading, npc.State);
        npc.SetExpression(1003, hold: true);
        npc.SetLook(LookPart.Head, LookMode.Free, false);
        npc.Aim(LookPart.Head, 0.5f, 0f);
        npc.Moment = 0.4f;
        npc.Yaw = 90f;
        Assert.Empty(r.Backend.Model(3).Calls);
        r.Backend.PendingLoads[0](true);
        Assert.Equal(PoseTargetState.Ready, npc.State);
        Assert.Equal(new[] { "play 9020", "face 1003 hold=True", "look Head Free lock=False", "aim Head 0.50,0.00", "yaw 90", "moment 0.40" },
            r.Backend.Model(3).Calls);
        Assert.Single(r.Backend.Opened);
    }

    [Fact]
    public void A_failed_load_fails_until_reset_and_controls_do_nothing()
    {
        var r = new PosingRig();
        var npc = r.Target(3);
        npc.PlayAction(9020);
        r.Backend.PendingLoads[0](false);
        Assert.Equal(PoseTargetState.Failed, npc.State);
        Assert.Equal(PoseResult.Unavailable, npc.PlayAction(9020));
        npc.Reset();
        Assert.Equal(1, r.Backend.Model(3).CloseCount);
        Assert.Equal(PoseTargetState.Idle, npc.State);
        npc.PlayAction(9020);
        Assert.Equal(2, r.Backend.Opened.Count);   // tries again with a new model
    }

    [Fact]
    public void Reset_undoes_exactly_what_was_touched_and_the_next_control_starts_fresh()
    {
        var r = new PosingRig();
        var self = r.Target(1);
        self.Yaw = 20f;
        self.SetLook(LookPart.Head, LookMode.Lens, false);
        self.Reset();
        Assert.Equal(PoseTouches.Head | PoseTouches.Yaw, r.Backend.Model(1).Closed);
        Assert.Equal(PoseTargetState.Idle, self.State);
        Assert.Equal(0f, self.Yaw);
        self.PlayAction(9020);
        Assert.Equal(2, r.Backend.Opened.Count);
        Assert.Equal(new[] { "play 9020" }, r.Backend.Model(1).Calls);
    }

    [Fact]
    public void Reset_of_an_untouched_person_never_makes_a_model()
    {
        var r = new PosingRig();
        r.Target(2).Reset();
        Assert.Empty(r.Backend.Opened);
    }

    [Fact]
    public void Freeze_freezes_every_posed_model_and_unfreeze_restores_it_then_reapplies_pauses()
    {
        var r = new PosingRig();
        var paused = r.Target(1);
        var playing = r.Target(2);
        paused.PlayAction(9020);
        paused.Moment = 0.3f;
        playing.PlayAction(9021);
        r.Backend.Model(1).Calls.Clear();
        r.Backend.Model(2).Calls.Clear();
        r.Freeze.Raise(true);
        Assert.Equal(new[] { "frozen True" }, r.Backend.Model(1).Calls);
        r.Freeze.Raise(false);
        Assert.Equal(new[] { "frozen True", "frozen False", "moment 0.30" }, r.Backend.Model(1).Calls);
        Assert.Equal(new[] { "frozen True", "frozen False" }, r.Backend.Model(2).Calls);
    }

    [Fact]
    public void A_repeated_freeze_signal_freezes_once()
    {
        var r = new PosingRig();
        r.Target(2).Yaw = 5f;
        r.Freeze.Raise(true);
        r.Freeze.Raise(true);   // ISceneFreeze.Changed also fires when only HoldsPositions flips
        Assert.Single(r.Backend.Model(2).Calls, "frozen True");
    }

    [Fact]
    public void A_person_posed_while_frozen_freezes_as_soon_as_their_model_is_ready()
    {
        var r = new PosingRig();
        r.Freeze.Raise(true);
        r.Target(2).Yaw = 5f;
        Assert.Equal(new[] { "yaw 5", "frozen True" }, r.Backend.Model(2).Calls);
        r.Target(3).PlayAction(9020);
        Assert.Empty(r.Backend.Model(3).Calls);
        r.Backend.PendingLoads[0](true);
        Assert.Equal(new[] { "play 9020", "frozen True" }, r.Backend.Model(3).Calls);
    }

    [Fact]
    public void Releasing_while_frozen_unfreezes_the_model_before_closing_it()
    {
        var r = new PosingRig();
        r.Target(2).Yaw = 5f;
        r.Freeze.Raise(true);
        r.Camera.ReleaseAll(CameraReleaseReason.SceneChanged);
        Assert.Equal(new[] { "yaw 5", "frozen True", "frozen False", "close Yaw" }, r.Backend.Model(2).Calls);
    }

    [Fact]
    public void Copies_and_models_report_where_they_stand_and_you_do_not()
    {
        var r = new PosingRig();
        r.Target(1).Yaw = 5f;
        Assert.False(r.Svc.TryGetVisiblePosition(new EntityId(1), out _));   // you: the camera follows your entity
        Assert.False(r.Svc.TryGetVisiblePosition(new EntityId(2), out _));   // not posed yet
        r.Target(2).Yaw = 5f;
        Assert.True(r.Svc.TryGetVisiblePosition(new EntityId(2), out var copy));
        Assert.Equal((3f, 0f, 4f), (copy.X, copy.Y, copy.Z));
        r.Target(3).PlayAction(9020);
        Assert.False(r.Svc.TryGetVisiblePosition(new EntityId(3), out _));   // still loading
        r.Backend.PendingLoads[0](true);
        Assert.True(r.Svc.TryGetVisiblePosition(new EntityId(3), out _));
        r.Camera.ReleaseAll(CameraReleaseReason.Cutscene);
        Assert.False(r.Svc.TryGetVisiblePosition(new EntityId(2), out _));
    }

    [Fact]
    public void Copies_are_capped_below_the_games_member_limit()
    {
        var r = new PosingRig();
        r.Backend.Limit = 3;   // the member list holds you + 2 other players
        r.Target(1).Yaw = 1f;
        r.Target(2).Yaw = 1f;
        r.Target(4).Yaw = 1f;
        var third = r.Target(5);
        Assert.Equal(PoseResult.Full, third.PlayAction(9020));
        Assert.Equal(PoseTargetState.Full, third.State);
        Assert.Equal(3, r.Backend.Opened.Count);
        r.Target(2).Reset();   // frees a slot
        Assert.Equal(PoseResult.Applied, third.PlayAction(9020));
        Assert.Equal(PoseTargetState.Ready, third.State);
        Assert.Equal(4, r.Backend.Opened.Count);
    }

    [Fact]
    public void Npc_models_have_their_own_cap_and_a_loading_model_counts()
    {
        var r = new PosingRig();
        r.Backend.Limit = 2;   // NPC partners: 2; other players: 1
        r.Target(3).PlayAction(9020);   // loading — still takes a slot
        r.Target(6).PlayAction(9020);
        Assert.Equal(PoseResult.Full, r.Target(7).PlayAction(9020));
        r.Target(2).Yaw = 1f;
        r.Target(4).Yaw = 1f;
        Assert.Equal(PoseTargetState.Full, r.Target(4).State);
        Assert.Equal(PoseTargetState.Ready, r.Target(2).State);
    }

    [Fact]
    public void The_member_limit_is_read_once_found_and_defaults_to_30_until_then()
    {
        var r = new PosingRig();
        r.Backend.Limit = 0;   // Lua not ready yet
        Assert.Equal(30, r.Svc.MemberLimit);
        Assert.Equal(30, r.Svc.MemberLimit);
        Assert.Equal(2, r.Backend.LimitReads);
        r.Backend.Limit = 12;
        Assert.Equal(12, r.Svc.MemberLimit);
        Assert.Equal(12, r.Svc.MemberLimit);
        Assert.Equal(3, r.Backend.LimitReads);
    }

    [Fact]
    public void A_look_change_on_a_paused_person_reapplies_the_pause()
    {
        var r = new PosingRig();
        var t = r.Target(2);
        t.PlayAction(9020);
        t.Moment = 0.5f;
        t.SetLook(LookPart.Head, LookMode.Free, false);
        t.Aim(LookPart.Head, 0.25f, 0f);
        Assert.Equal(new[] { "play 9020", "moment 0.50", "look Head Free lock=False", "aim Head 0.00,0.00", "moment 0.50",
            "aim Head 0.25,0.00", "moment 0.50" }, r.Backend.Model(2).Calls);
    }

    [Fact]
    public void Moment_reads_the_game_only_while_playing()
    {
        var r = new PosingRig();
        var t = r.Target(2);
        Assert.Equal(-1f, t.Moment);
        t.Moment = 0.6f;   // ignored before an action
        Assert.Empty(r.Backend.Opened);
        t.PlayAction(9020);
        Assert.Equal(0.25f, t.Moment);   // live readback
        t.Moment = 0.7f;
        Assert.Equal(0.7f, t.Moment);
        Assert.Single(r.Backend.Model(2).Calls, "read");
    }

    [Fact]
    public void Closing_while_an_npc_model_loads_closes_it_and_ignores_the_late_load()
    {
        var r = new PosingRig();
        var npc = r.Target(3);
        npc.PlayAction(9020);
        r.Camera.ReleaseAll(CameraReleaseReason.SceneChanged);
        Assert.Equal(1, r.Backend.Model(3).CloseCount);
        r.Backend.PendingLoads[0](true);
        Assert.Equal(PoseTargetState.Released, npc.State);
        Assert.DoesNotContain(r.Backend.Model(3).Calls, c => c.StartsWith("play", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refused_first_action_reports_Refused()
    {
        var r = new PosingRig();
        r.Backend.RefuseActions = true;
        Assert.Equal(PoseResult.Refused, r.Target(1).PlayAction(9020));
        Assert.Equal(PoseResult.Refused, r.Target(1).PlayAction(9020));
    }

    [Fact]
    public void Expressions_are_read_once_when_found_and_unknown_ids_are_ignored()
    {
        var r = new PosingRig();
        Assert.Equal(2, r.Svc.Expressions.Count);
        Assert.Equal(2, r.Svc.Expressions.Count);
        Assert.Equal(1, r.Backend.ExpressionReads);
        var t = r.Target(2);
        t.SetExpression(4242, hold: true);
        Assert.Empty(r.Backend.Opened);
        t.SetExpression(1015, hold: false);
        t.SetExpression(0, hold: true);
        Assert.Equal(new[] { "face 1015 hold=False", "face 0 hold=True" }, r.Backend.Model(2).Calls);
    }

    [Fact]
    public void An_empty_expression_list_is_read_again_next_time()
    {
        var r = new PosingRig();
        r.Backend.ExpressionList = new();
        Assert.Empty(r.Svc.Expressions);
        Assert.Empty(r.Svc.Expressions);
        Assert.Equal(2, r.Backend.ExpressionReads);
    }

    [Fact]
    public void A_throwing_Changed_handler_never_breaks_a_release()
    {
        var r = new PosingRig();
        r.Svc.Changed += () => throw new InvalidOperationException("plugin handler");
        r.Target(2).Yaw = 5f;
        r.Camera.ReleaseAll(CameraReleaseReason.Cutscene);
        Assert.Equal(PoseTouches.Yaw, r.Backend.Model(2).Closed);
        Assert.Contains(r.Warnings, w => w.Contains("Changed handler threw"));
    }

    [Fact]
    public void People_come_from_one_backend_read_and_are_empty_without_a_camera()
    {
        var r = new PosingRig(acquire: false);
        Assert.Empty(r.Svc.NearbyPeople(40f));
        r.Acquire();
        Assert.Equal(new[] { "Revette", "Celia" }, r.Svc.NearbyPeople(40f).Select(p => p.Name));
    }

    [Fact]
    public void Another_owner_cannot_take_a_selected_person_and_owners_release_apart()
    {
        var r = new PosingRig();
        object a = new(), b = new();
        var mine = r.Svc.Select(new EntityId(2), a)!;
        Assert.Null(r.Svc.Select(new EntityId(2), b));
        var theirs = r.Svc.Select(new EntityId(1), b)!;
        mine.Yaw = 10f;
        theirs.Yaw = 10f;
        r.Svc.ReleaseOwner(a);
        Assert.Equal(PoseTargetState.Released, mine.State);
        Assert.Equal(PoseTargetState.Ready, theirs.State);
    }

    // Review round 1, finding 1: the game's own play-from-paused path always clears the held persist time
    // (SetActionPersistTime(-1)) before the next play (recon docs/recon/photo-posing-recon.md "Pose play from
    // paused"). A new action on a paused, Ready person must do the same before PlayAction.
    [Fact]
    public void Playing_a_new_action_while_paused_clears_the_held_moment_first()
    {
        var r = new PosingRig();
        var t = r.Target(2);
        t.PlayAction(9020);
        t.Moment = 0.5f;
        r.Backend.Model(2).Live = -1f;   // the fresh play reports no progress yet
        t.PlayAction(9021);
        Assert.Equal(-1f, t.Moment);
        Assert.Equal(new[] { "play 9020", "moment 0.50", "moment -1.00", "play 9021", "read" }, r.Backend.Model(2).Calls);
    }

    // Review round 1, finding 2: a synchronous open failure (self/player) still hands back a model — the half-made
    // copy keeps hiding the real person until it is closed, so a camera release must still unhide it exactly once.
    [Fact]
    public void A_failed_open_on_a_player_still_gets_closed_on_camera_release()
    {
        var r = new PosingRig();
        r.Backend.FailOpen = true;
        var copy = r.Target(2);
        copy.PlayAction(9020);
        Assert.Equal(PoseTargetState.Failed, copy.State);
        r.Camera.ReleaseAll(CameraReleaseReason.SceneChanged);
        Assert.Equal(1, r.Backend.Model(2).CloseCount);
    }

    // Review round 1, finding 2 (cap decision, pinned): the half-made copy behind a Failed target still holds a
    // photo-member slot (it still hides a player) until Reset() closes it — a second player must see Full, and
    // resetting the failed one frees the slot.
    [Fact]
    public void A_failed_open_still_counts_toward_the_cap_until_reset()
    {
        var r = new PosingRig();
        r.Backend.Limit = 2;   // other players cap at limit - 1 = 1
        r.Backend.FailOpen = true;
        var failed = r.Target(2);
        failed.PlayAction(9020);
        Assert.Equal(PoseTargetState.Failed, failed.State);
        r.Backend.FailOpen = false;
        var another = r.Target(4);
        Assert.Equal(PoseResult.Full, another.PlayAction(9020));
        failed.Reset();
        Assert.Equal(PoseResult.Applied, another.PlayAction(9020));
        Assert.Equal(PoseTargetState.Ready, another.State);
    }

    // Review round 1, finding 4: OnFreezeChanged / ReapplyPauses iterate _targets while running backend code
    // (SetFrozen / ReapplyPause). A plugin reacting to Changed (or a despawn's RaiseChanged) can reentrantly Select()
    // a new person, which ADDS to _targets mid-enumeration — a Dictionary<,> enumerator throws
    // InvalidOperationException on the next MoveNext after an insert (measured: a bare Remove alone does not throw,
    // but an Add during enumeration reliably does). Snapshot (like CloseWhere) so it cannot.
    [Fact]
    public void A_reentrant_Select_from_inside_the_freeze_loop_does_not_corrupt_it()
    {
        var r = new PosingRig();
        var a = r.Target(2);
        var b = r.Target(4);
        a.Yaw = 1f;
        b.Yaw = 1f;
        r.Backend.Model(2).OnFrozenCalled = () =>
        {
            var extra = r.Svc.Select(new EntityId(5));   // a plugin reacting mid-call, adding a brand-new target
            extra!.Yaw = 1f;
        };
        r.Freeze.Raise(true);   // must not throw "Collection was modified"
        Assert.Equal(PoseTargetState.Ready, a.State);
        Assert.Equal(PoseTargetState.Ready, b.State);
        Assert.Contains("frozen True", r.Backend.Model(4).Calls);
    }

    // Task 6 review carry-over (C): a refused open (the backend made nothing — e.g. inside the scene-change settle
    // window it hands back the shared DeadPoseModel) must NOT hold a photo-member slot, unlike the half-made copy above.
    [Fact]
    public void A_refused_open_does_not_hold_a_cap_slot()
    {
        var r = new PosingRig();
        r.Backend.Limit = 2;   // other players cap at limit - 1 = 1
        r.Backend.RefuseOpen = true;
        var refused = r.Target(2);
        refused.PlayAction(9020);
        Assert.Equal(PoseTargetState.Failed, refused.State);
        r.Backend.RefuseOpen = false;
        var another = r.Target(4);
        Assert.Equal(PoseResult.Applied, another.PlayAction(9020));
        Assert.Equal(PoseTargetState.Ready, another.State);
    }

    // Task 6 review carry-over (B): a despawn with no one selected is the cheap path (every entity removal reaches it).
    [Fact]
    public void A_despawn_with_no_targets_does_nothing()
    {
        var r = new PosingRig();
        var changed = 0;
        r.Svc.Changed += () => changed++;
        r.Backend.Remove(2);
        Assert.Equal(0, changed);
        Assert.Empty(r.Warnings);
    }
}
