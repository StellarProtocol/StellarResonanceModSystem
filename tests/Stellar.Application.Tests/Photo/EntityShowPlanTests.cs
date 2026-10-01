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

    // Re-pin of fix round 2 (minor 3) "Reset forgets the mirror so the hold is rewritten": under the counter model a
    // re-write of a held type is a DOUBLE hide that one release can never undo. The counters live in ZEntityMgr, not
    // in CameraFrameCtrl, so a CameraFrameCtrl re-init no longer resets anything — held types are never re-hidden.
    [Fact]
    public void No_reset_path_can_double_hide_a_held_type()
    {
        var p = new EntityShowPlan();
        p.Apply(true, false, Write);
        p.Apply(true, false, Write);
        p.Apply(false, false, Write);
        Assert.Equal(0, Count(11));
    }
}
