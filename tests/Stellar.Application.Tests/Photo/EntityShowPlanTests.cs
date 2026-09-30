using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// Fix round 1 (#2): our other-player / party hides share CameraFrameCtrl.SetEntityShow with the game's own camera
// mode. The plan restores the game's PRIOR state exactly and handles KeepParty toggles while others stay hidden.
public sealed class EntityShowPlanTests
{
    private readonly List<(int Type, bool Show)> _calls = new();
    private bool Write(int type, bool show) { _calls.Add((type, show)); return true; }

    [Fact]
    public void Hide_without_keep_party_hides_other_players_and_team()
    {
        var p = new EntityShowPlan();
        Assert.True(p.Apply(hideOthers: true, keepParty: false, Write));
        Assert.Equal(new[] { (11, false), (3, false) }, _calls);
    }

    [Fact]
    public void Hide_with_keep_party_leaves_team_untouched()
    {
        var p = new EntityShowPlan();
        p.Apply(true, true, Write);
        Assert.Equal(new[] { (11, false) }, _calls);
    }

    [Fact]
    public void Clearing_keep_party_while_others_stay_hidden_rehides_team()
    {
        var p = new EntityShowPlan();
        p.Apply(true, true, Write);
        _calls.Clear();
        p.Apply(true, false, Write);
        Assert.Equal(new[] { (3, false) }, _calls);
    }

    [Fact]
    public void Setting_keep_party_restores_team_prior_state()
    {
        var p = new EntityShowPlan();
        p.ObserveGame(3, false);          // the player had hidden the team in the game's camera UI
        p.Apply(true, false, Write);
        _calls.Clear();
        p.Apply(true, true, Write);
        Assert.Empty(_calls);             // team's prior was hidden and it is still hidden — nothing to restore
        p.ObserveGame(3, true);
        _calls.Clear();
        p.Apply(false, false, Write);
        Assert.Equal(new[] { (11, true) }, _calls);
    }

    [Fact]
    public void Release_restores_the_game_prior_state_exactly()
    {
        var p = new EntityShowPlan();
        p.ObserveGame(11, false);         // the game had other players hidden before our hide
        p.Apply(true, false, Write);
        _calls.Clear();
        p.Apply(false, false, Write);
        Assert.Equal(new[] { (3, true) }, _calls);   // 11 stays hidden (its prior), team comes back
    }

    [Fact]
    public void Game_write_while_held_is_undone_by_reapply()
    {
        var p = new EntityShowPlan();
        p.Apply(true, false, Write);
        p.ObserveGame(11, true);          // the game's camera mode showed them again
        _calls.Clear();
        p.Apply(true, false, Write);      // re-assert
        Assert.Equal(new[] { (11, false) }, _calls);
        _calls.Clear();
        p.Apply(false, false, Write);     // release → the game's last own value (shown)
        Assert.Equal(new[] { (11, true), (3, true) }, _calls);
    }

    [Fact]
    public void Failed_write_is_retried_on_the_next_apply()
    {
        var p = new EntityShowPlan();
        Assert.False(p.Apply(true, false, (_, _) => false));
        p.Apply(true, false, Write);
        Assert.Equal(new[] { (11, false), (3, false) }, _calls);
    }

    // Fix round 2 (minor 3): CameraFrameCtrl re-init may reset the game's flags; the plan forgets its mirror and the
    // next apply re-writes the held state instead of trusting a stale "already hidden".
    [Fact]
    public void Reset_forgets_the_mirror_so_the_hold_is_rewritten()
    {
        var p = new EntityShowPlan();
        p.Apply(true, false, Write);
        p.Reset();
        _calls.Clear();
        p.Apply(true, false, Write);
        Assert.Equal(new[] { (11, false), (3, false) }, _calls);
    }
}
