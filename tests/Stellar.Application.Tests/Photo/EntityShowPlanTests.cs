using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// Owner bug 2026-10-01 "Keep my party visible hides the party anyway" (recon: devkit
// .superpowers/sdd/recon-party-grain.md). CameraFrameCtrl.SetEntityShow drives per-source REFCOUNTS in
// ZEntityMgr (hide +1, show -1 clamped at 0), shared with the game's own camera Show panel, and OtherPlayer (11)
// is a master switch checked BEFORE any relation — so Team (3) can never rescue a teammate while 11 is hidden.
// The plan therefore hides Stranger(6)+Chum(2)+Union(4) to keep the party, 11 only to hide everyone, and keeps
// hide-once/show-once bookkeeping of only the types WE hid (the counter composes with the game's own holds).
public sealed class EntityShowPlanTests
{
    // Simulates ZEntityMgr's per-type counters for the ETakePhotos source.
    private readonly Dictionary<int, int> _count = new();
    private readonly List<(int Type, bool Show)> _calls = new();

    private bool Write(int type, bool show)
    {
        _calls.Add((type, show));
        _count.TryGetValue(type, out var c);
        _count[type] = show ? System.Math.Max(c - 1, 0) : c + 1;
        return true;
    }

    private int Count(int type) => _count.TryGetValue(type, out var c) ? c : 0;

    [Fact]
    public void Keep_party_hides_stranger_chum_union_and_never_writes_other_player_or_team()
    {
        var p = new EntityShowPlan();
        Assert.True(p.Apply(hideOthers: true, keepParty: true, Write));
        Assert.Equal(new[] { (6, false), (2, false), (4, false) }, _calls);
        Assert.DoesNotContain(_calls, c => c.Type is 11 or 3);
    }

    [Fact]
    public void Keep_party_release_shows_each_type_exactly_once()
    {
        var p = new EntityShowPlan();
        p.Apply(true, true, Write);
        _calls.Clear();
        p.Apply(false, false, Write);
        Assert.Equal(new[] { (6, true), (2, true), (4, true) }, _calls);
        Assert.Equal(0, Count(6) + Count(2) + Count(4));
    }

    [Fact]
    public void Hide_everyone_uses_only_the_other_player_master_switch()
    {
        var p = new EntityShowPlan();
        p.Apply(true, false, Write);
        Assert.Equal(new[] { (11, false) }, _calls);
        _calls.Clear();
        p.Apply(false, false, Write);
        Assert.Equal(new[] { (11, true) }, _calls);
    }

    [Fact]
    public void Switching_keep_party_while_hidden_releases_the_old_set_then_hides_the_new()
    {
        var p = new EntityShowPlan();
        p.Apply(true, false, Write);
        _calls.Clear();
        p.Apply(true, true, Write);
        Assert.Equal(new[] { (11, true), (6, false), (2, false), (4, false) }, _calls);
        _calls.Clear();
        p.Apply(true, false, Write);
        Assert.Equal(new[] { (6, true), (2, true), (4, true), (11, false) }, _calls);
        Assert.Equal(1, Count(11));
        Assert.Equal(0, Count(6));
    }

    [Fact]
    public void Reapply_of_a_held_set_never_double_hides()
    {
        var p = new EntityShowPlan();
        p.Apply(true, true, Write);
        _calls.Clear();
        p.Apply(true, true, Write);   // re-assert (a hide target was rebuilt)
        p.Apply(true, true, Write);
        Assert.Empty(_calls);
        Assert.Equal(1, Count(6));
        Assert.False(p.NeedsWrite(true, true));
    }

    [Fact]
    public void The_games_own_camera_panel_hide_survives_our_hide_and_release()
    {
        var p = new EntityShowPlan();
        _count[6] = 1;                 // the player hid Stranger in the game's own camera Show panel
        p.Apply(true, true, Write);    // ours hides once …
        p.Apply(false, false, Write);  // … and shows once
        Assert.Equal(1, Count(6));     // the game's hide is still in force
        Assert.Single(_calls, c => c == (6, false));
        Assert.Single(_calls, c => c == (6, true));
    }

    [Fact]
    public void Release_with_nothing_held_writes_nothing()
    {
        var p = new EntityShowPlan();
        Assert.True(p.Apply(false, true, Write));
        Assert.Empty(_calls);
        Assert.False(p.NeedsWrite(false, false));
    }

    [Fact]
    public void A_failed_hide_is_not_held_and_is_retried_on_the_next_apply()
    {
        var p = new EntityShowPlan();
        Assert.False(p.Apply(true, false, (_, _) => false));
        Assert.True(p.NeedsWrite(true, false));
        p.Apply(true, false, Write);
        Assert.Equal(new[] { (11, false) }, _calls);
    }

    [Fact]
    public void A_failed_show_stays_held_and_is_retried_so_the_hide_is_never_stranded()
    {
        var p = new EntityShowPlan();
        p.Apply(true, false, Write);
        Assert.False(p.Apply(false, false, (_, _) => false));
        Assert.True(p.NeedsWrite(false, false));
        _calls.Clear();
        p.Apply(false, false, Write);
        Assert.Equal(new[] { (11, true) }, _calls);
        Assert.Equal(0, Count(11));
    }

    [Fact]
    public void Holds_keep_party_set_reports_only_the_party_preserving_hide()
    {
        var p = new EntityShowPlan();
        Assert.False(p.HoldsKeepPartySet);
        p.Apply(true, false, Write);
        Assert.False(p.HoldsKeepPartySet);
        p.Apply(true, true, Write);
        Assert.True(p.HoldsKeepPartySet);
        p.Apply(false, false, Write);
        Assert.False(p.HoldsKeepPartySet);
    }

    // Fix round 2 (minor 3) origin, restated for the counter model: the game can reset the ZEntityMgr hold counters
    // under us (a re-init), silently dropping our hide. The plan reads the live count (getHideCount, photo source)
    // and re-hides a held type exactly once when its count reads 0 — never on a count > 0 (that would be a double hide).
    private int? LiveCount(int type) => Count(type);

    [Fact]
    public void Counter_reset_while_held_rehides_exactly_once()
    {
        var p = new EntityShowPlan();
        p.Apply(true, false, Write, LiveCount);
        _count.Clear();                                 // the game reset its counters: our hide is gone
        Assert.True(p.NeedsWrite(true, false, LiveCount));
        _calls.Clear();
        p.Apply(true, false, Write, LiveCount);
        p.Apply(true, false, Write, LiveCount);         // a second re-assert must not hide again
        Assert.Equal(new[] { (11, false) }, _calls);
        Assert.Equal(1, Count(11));
    }

    [Fact]
    public void Counter_reset_of_the_keep_party_set_rehides_each_type_once()
    {
        var p = new EntityShowPlan();
        p.Apply(true, true, Write, LiveCount);
        _count[2] = 0;                                  // only Chum's counter was reset
        _calls.Clear();
        p.Apply(true, true, Write, LiveCount);
        Assert.Equal(new[] { (2, false) }, _calls);
    }

    [Fact]
    public void Count_zero_at_release_issues_no_show()
    {
        var p = new EntityShowPlan();
        _count[11] = 0;
        p.Apply(true, false, Write, LiveCount);
        _count[11] = 0;                                 // reset while held, before the release
        _calls.Clear();
        Assert.True(p.Apply(false, false, Write, LiveCount));
        Assert.Empty(_calls);                           // a show here could cancel someone else's later hold
        Assert.False(p.NeedsWrite(false, false, LiveCount));
    }

    [Fact]
    public void Unreadable_count_keeps_the_bookkeeping_behaviour()
    {
        var p = new EntityShowPlan();
        int? Unreadable(int _) => null;
        p.Apply(true, false, Write, Unreadable);
        _calls.Clear();
        p.Apply(true, false, Write, Unreadable);        // no re-hide on an unknown count
        Assert.Empty(_calls);
        Assert.False(p.NeedsWrite(true, false, Unreadable));
        p.Apply(false, false, Write, Unreadable);       // release still shows exactly once
        Assert.Equal(new[] { (11, true) }, _calls);
    }

    [Theory]
    [InlineData(6, 6)]    // Stranger → Nearby
    [InlineData(2, 5)]    // Chum → Friend
    [InlineData(4, 3)]    // Union → Union
    [InlineData(11, 7)]   // OtherPlayer → OtherPlayer
    [InlineData(14, 12)]  // SelfPet → SelfPet (owner MAIN 2026-10-03)
    // Measured on the owner's MAIN client 2026-10-07 (one switch at a time, "[PhotoVis] holds after entity write …") and
    // read from the native CameraFrameCtrl.SetEntityShow switch table (0x184255334) — both agree:
    [InlineData(7, 1)]    // FriendlyNPCS → Npc
    [InlineData(9, 2)]    // Enemy → Monster
    [InlineData(3, 4)]    // Team → Team
    [InlineData(12, 9)]   // Collection → Collection
    [InlineData(15, 13)]  // OtherPet → OtherPet
    public void Camera_types_map_to_the_render_layer_hide_types(int camera, int layer) =>
        Assert.Equal(layer, EntityShowPlan.HideTypeFor(camera));

    [Fact]
    public void Self_target_hides_oneself_and_self_pet_once_and_shows_each_once()
    {
        var p = new EntityShowPlan();
        Assert.True(p.Apply(EntityShowPlan.SelfSet, Write));
        Assert.Equal(new[] { (1, false), (14, false) }, _calls);
        _calls.Clear();
        Assert.False(p.NeedsWrite(EntityShowPlan.SelfSet));
        Assert.True(p.Apply(System.Array.Empty<int>(), Write));
        Assert.Equal(new[] { (1, true), (14, true) }, _calls);
        Assert.Equal(0, Count(1) + Count(14));
    }

    [Fact]
    public void Self_and_other_player_plans_are_independent()
    {
        var self = new EntityShowPlan();
        var others = new EntityShowPlan();
        self.Apply(EntityShowPlan.SelfSet, Write);
        others.Apply(true, false, Write);
        Assert.False(self.HoldsKeepPartySet);
        Assert.Equal(1, Count(11));
        Assert.Equal(1, Count(1));
    }

    [Fact]
    public void Unknown_camera_type_has_no_hide_type()
    {
        Assert.Null(EntityShowPlan.TryHideTypeFor(99));
        Assert.Equal(7, EntityShowPlan.TryHideTypeFor(EntityShowPlan.OtherPlayer));
    }

    // Measured on the owner's client 2026-10-03 ([PhotoVis] holds after Self hide=True: SelfPet(12)=1): SetEntityShow(1)
    // + SetEntityShow(14) move exactly one ETakePhotos counter, EntityRenderLayerHideType.SelfPet = 12. Oneself (1)
    // keeps no counter in that source, so it stays unmapped (bookkeeping only, no reset check).
    [Fact]
    public void Self_pet_maps_to_the_measured_hide_type_and_oneself_has_none()
    {
        Assert.Equal(12, EntityShowPlan.TryHideTypeFor(EntityShowPlan.SelfPet));
        Assert.Null(EntityShowPlan.TryHideTypeFor(EntityShowPlan.Oneself));
    }

    // M2 follow-up: HasPendingRestore reads this to notice a failed show-back even though nothing is held "cleanly".
    [Fact]
    public void HoldsAny_reflects_whatever_is_currently_held()
    {
        var p = new EntityShowPlan();
        Assert.False(p.HoldsAny);
        p.Apply(true, false, Write);
        Assert.True(p.HoldsAny);
        p.Apply(false, false, Write);
        Assert.False(p.HoldsAny);
    }

    [Fact]
    public void HoldsAny_stays_true_when_a_show_fails()
    {
        var p = new EntityShowPlan();
        p.Apply(true, false, Write);
        Assert.False(p.Apply(false, false, (_, _) => false));   // the show fails
        Assert.True(p.HoldsAny);
    }

    // Me (Oneself 1) and Weapon (WeaponsAppearance 10) are per-entity on/off BITS on the local player, not counters (native
    // SetEntityShow cases 0x184254E61 / 0x1842550FB; owner MAIN 2026-10-07: neither leaves an ETakePhotos counter). The
    // game's photo-screen exit writes them back (ResetCameraInitialParameters → ChangeWeaponVisible(default, EPhoto);
    // ResetEntityVisible → SetEntityShow(1, true) when the game hid Me), so a re-assert re-writes OUR held flags — and only
    // those: a counter type is never re-written (a second hide would stack a hold).
    [Fact]
    public void Only_oneself_and_weapons_are_flag_types_and_neither_has_a_counter()
    {
        for (var t = 0; t <= 15; t++)
            Assert.Equal(t is 1 or 10, EntityShowPlan.IsFlagType(t));
        Assert.Null(EntityShowPlan.TryHideTypeFor(1));
        Assert.Null(EntityShowPlan.TryHideTypeFor(10));
    }

    [Fact]
    public void Rewrite_held_flags_rehides_only_held_flag_types_and_is_idempotent()
    {
        var p = new EntityShowPlan();
        p.Apply(new[] { 1, 10, 6, 14 }, Write);
        _calls.Clear();
        Assert.Equal(2, p.RewriteHeldFlags(new[] { 1, 10, 6, 14 }, Write));
        Assert.Equal(new[] { (1, false), (10, false) }, _calls);
        Assert.Equal(2, p.RewriteHeldFlags(new[] { 1, 10, 6, 14 }, Write));   // again: same writes, nothing stacks in game
        Assert.True(p.Holds(1) && p.Holds(10) && p.Holds(6));
        Assert.False(p.NeedsWrite(new[] { 1, 10, 6, 14 }));                  // bookkeeping untouched
    }

    [Fact]
    public void Rewrite_held_flags_skips_a_flag_type_not_held_or_not_wanted()
    {
        var p = new EntityShowPlan();
        p.Apply(new[] { 10 }, (t, show) => t != 10 && Write(t, show));   // the Weapon hide failed: not held
        _calls.Clear();
        Assert.Equal(0, p.RewriteHeldFlags(new[] { 10 }, Write));
        p.Apply(new[] { 1 }, Write);
        _calls.Clear();
        Assert.Equal(0, p.RewriteHeldFlags(new[] { 6 }, Write));          // held but no longer in the target
        Assert.Empty(_calls);
    }
}
