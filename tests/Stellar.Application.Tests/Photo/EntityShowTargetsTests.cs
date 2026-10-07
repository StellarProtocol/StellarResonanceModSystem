using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// Framework 2.20.0 (player request "Photo Studio - FOV Slider and Toggling Collectibles/Spirit Echo"): every switch of
// the game's own photo screen hide list, transcribed from camerasys_data.lua ShowEntityCfg (release_3.7):
// Self 1, SelfPet 14, Stranger 6, FriendlyNPCS 7, Enemy 9, WeaponsAppearance 10, Chum 2, Team 3, Union 4,
// Collection 12, OtherPet 15. The legacy layers (OtherPlayers / KeepParty / Self) must keep their exact pre-2.20 types —
// the owner-reported "Keep my party visible hides the party anyway" fix (2026-10-01) and Self = me + pet + Battle Imagine
// (owner MAIN pass 2026-10-03) are pinned here as well as in EntityShowPlanTests.
public sealed class EntityShowTargetsTests
{
    [Theory]
    [InlineData(VisibilityLayers.SelfCharacter, 1)]
    [InlineData(VisibilityLayers.OwnSpiritEcho, 14)]
    [InlineData(VisibilityLayers.Strangers, 6)]
    [InlineData(VisibilityLayers.NonPlayers, 7)]
    [InlineData(VisibilityLayers.Enemies, 9)]
    [InlineData(VisibilityLayers.Weapons, 10)]      // the game's Weapon toggle writes WeaponsAppearance, never AngelWeapons (8)
    [InlineData(VisibilityLayers.Friends, 2)]
    [InlineData(VisibilityLayers.Party, 3)]
    [InlineData(VisibilityLayers.Guild, 4)]
    [InlineData(VisibilityLayers.Collectibles, 12)]
    [InlineData(VisibilityLayers.OtherSpiritEchoes, 15)]
    public void Each_photo_screen_switch_drives_exactly_its_camera_type(VisibilityLayers layer, int type) =>
        Assert.Equal(new[] { type }, EntityShowTargets.For(layer));

    [Fact]
    public void Legacy_layers_keep_their_pre_2_20_types()
    {
        Assert.Equal(new[] { 11 }, EntityShowTargets.For(VisibilityLayers.OtherPlayers));
        Assert.Equal(new[] { 6, 2, 4 }, EntityShowTargets.For(VisibilityLayers.OtherPlayers | VisibilityLayers.KeepParty));
        Assert.Equal(new[] { 1, 14 }, EntityShowTargets.For(VisibilityLayers.Self));
        Assert.Empty(EntityShowTargets.For(VisibilityLayers.KeepParty));   // a modifier alone drives nothing
    }

    [Fact]
    public void Non_world_layers_drive_nothing()
    {
        var other = VisibilityLayers.GameHud | VisibilityLayers.StellarOverlay | VisibilityLayers.Nameplates | VisibilityLayerSets.Effects;
        Assert.Empty(EntityShowTargets.For(other));
        Assert.Equal(VisibilityLayers.None, other & EntityShowTargets.Layers);
    }

    [Fact]
    public void Overlapping_layers_share_one_hold_per_type()
    {
        var target = EntityShowTargets.For(VisibilityLayers.Self | VisibilityLayers.SelfCharacter | VisibilityLayers.OwnSpiritEcho);
        Assert.Equal(new[] { 1, 14 }, target);
        var keepParty = EntityShowTargets.For(VisibilityLayers.OtherPlayers | VisibilityLayers.KeepParty | VisibilityLayers.Friends | VisibilityLayers.Strangers);
        Assert.Equal(new[] { 6, 2, 4 }, keepParty);
    }

    [Fact]
    public void Every_layer_together_is_every_photo_type_once_with_other_players_first()
    {
        var all = EntityShowTargets.For(VisibilityLayerSets.World & ~VisibilityLayers.KeepParty);
        Assert.Equal(11, all[0]);
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 7, 9, 10, 11, 12, 14, 15 }, all.OrderBy(t => t));
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void Achieved_reports_a_layer_only_when_all_its_types_are_held()
    {
        var held = new HashSet<int> { 1, 6, 2 };   // 14 and 4 failed
        var requested = VisibilityLayers.Self | VisibilityLayers.SelfCharacter | VisibilityLayers.OtherPlayers |
                        VisibilityLayers.KeepParty | VisibilityLayers.Strangers | VisibilityLayers.Friends;
        var achieved = EntityShowTargets.Achieved(requested, held.Contains);
        Assert.Equal(VisibilityLayers.SelfCharacter | VisibilityLayers.Strangers | VisibilityLayers.Friends, achieved);
        held.UnionWith(new[] { 14, 4 });
        Assert.Equal(requested, EntityShowTargets.Achieved(requested, held.Contains));
    }

    [Fact]
    public void Keep_party_is_never_reported_without_an_achieved_other_players()
    {
        Assert.Equal(VisibilityLayers.None, EntityShowTargets.Achieved(VisibilityLayers.KeepParty, _ => true));
        Assert.Equal(VisibilityLayers.OtherPlayers, EntityShowTargets.Achieved(VisibilityLayers.OtherPlayers, t => t == 11));
    }

    // The plan driven by the union target: switching Me from the legacy Self to the split layers moves no hold
    // (both use Oneself + SelfPet), and releasing one of two layers that share a type keeps that type hidden.
    private readonly Dictionary<int, int> _count = new();
    private readonly List<(int Type, bool Show)> _calls = new();

    private bool Write(int type, bool show)
    {
        _calls.Add((type, show));
        _count.TryGetValue(type, out var c);
        _count[type] = show ? System.Math.Max(c - 1, 0) : c + 1;
        return true;
    }

    [Fact]
    public void Releasing_one_of_two_layers_sharing_a_type_keeps_the_type_hidden()
    {
        var p = new EntityShowPlan();
        p.Apply(EntityShowTargets.For(VisibilityLayers.Self), Write);
        _calls.Clear();
        p.Apply(EntityShowTargets.For(VisibilityLayers.SelfCharacter | VisibilityLayers.OwnSpiritEcho), Write);
        Assert.Empty(_calls);
        p.Apply(EntityShowTargets.For(VisibilityLayers.SelfCharacter), Write);
        Assert.Equal(new[] { (14, true) }, _calls);
        Assert.Equal(1, _count[1]);
        p.Apply(EntityShowTargets.For(VisibilityLayers.None), Write);
        Assert.Equal(0, _count[1] + _count[14]);
    }

    [Fact]
    public void Each_new_type_hides_once_and_shows_once()
    {
        var p = new EntityShowPlan();
        var everything = EntityShowTargets.For(VisibilityLayerSets.World & ~VisibilityLayers.KeepParty);
        Assert.True(p.Apply(everything, Write));
        p.Apply(everything, Write);   // a re-assert never double-hides
        Assert.True(p.Apply(EntityShowTargets.For(VisibilityLayers.None), Write));
        foreach (var type in everything)
        {
            Assert.Single(_calls, c => c == (type, false));
            Assert.Single(_calls, c => c == (type, true));
            Assert.Equal(0, _count[type]);
        }
    }

    [Fact]
    public void Holds_any_outside_flags_a_failed_show_back_still_owed()
    {
        var p = new EntityShowPlan();
        p.Apply(EntityShowTargets.For(VisibilityLayers.Collectibles), Write);
        Assert.False(p.HoldsAnyOutside(EntityShowTargets.For(VisibilityLayers.Collectibles)));
        Assert.False(p.Apply(EntityShowTargets.For(VisibilityLayers.None), (_, _) => false));
        Assert.True(p.HoldsAnyOutside(EntityShowTargets.For(VisibilityLayers.None)));
        Assert.True(p.Holds(12));
    }

    [Fact]
    public void Relation_hide_is_any_player_group_without_the_master_switch()
    {
        var p = new EntityShowPlan();
        p.Apply(EntityShowTargets.For(VisibilityLayers.Self | VisibilityLayers.Enemies), Write);
        Assert.False(p.HoldsRelationHide);
        p.Apply(EntityShowTargets.For(VisibilityLayers.Party), Write);
        Assert.True(p.HoldsRelationHide);
        p.Apply(EntityShowTargets.For(VisibilityLayers.OtherPlayers | VisibilityLayers.KeepParty | VisibilityLayers.Self), Write);
        Assert.True(p.HoldsRelationHide);   // keep-party + Self in one plan still refreshes (it used to be two plans)
        p.Apply(EntityShowTargets.For(VisibilityLayers.OtherPlayers | VisibilityLayers.Party), Write);
        Assert.False(p.HoldsRelationHide);  // the master switch hides everyone; nothing to refresh
    }
}
