using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

// The non-sticky stacking rule (WindowStacking): popups > Topmost > ZOrder > layer (HUD below Tools/Debug, a fronted
// HUD lifted until hidden) > activation (newest on top). Replaces the old sticky ZFront tier, under which a window that
// was EVER fronted outranked every never-fronted window forever (plugin windows opened under the launcher/Settings).
public sealed class WindowStackingTests
{
    private static WindowStackKey Tools(int act) => new(false, false, 0, WindowCategory.Tools, false, act);
    private static WindowStackKey Debug(int act) => new(false, false, 0, WindowCategory.Debug, false, act);
    private static WindowStackKey Hud(int act, bool lifted = false) => new(false, false, 0, WindowCategory.HUD, lifted, act);

    private static bool Above(WindowStackKey a, WindowStackKey b) => WindowStacking.Compare(a, b) > 0;

    [Fact]
    public void A_window_opened_after_a_fronted_one_goes_above_it()
    {
        var fronted = Tools(act: 5);    // e.g. the launcher, BringToFront'd at activation 5
        var opened = Tools(act: 6);     // a plugin window opened afterwards from a launcher tile
        Assert.True(Above(opened, fronted));
    }

    [Fact]
    public void Tools_and_Debug_share_one_layer_ordered_by_activation()
    {
        Assert.True(Above(Tools(act: 9), Debug(act: 3)));
        Assert.True(Above(Debug(act: 10), Tools(act: 9)));
    }

    [Fact]
    public void Hud_stays_below_tools_unless_lifted()
    {
        Assert.True(Above(Tools(act: 1), Hud(act: 50)));
        var lifted = Hud(act: 7, lifted: true);
        Assert.True(Above(lifted, Tools(act: 6)));    // fronted HUD surfaces above an older Tools window (Rule 5 use)
        Assert.True(Above(Tools(act: 8), lifted));    // ...but not stickily: a Tools window opened later beats it
    }

    [Fact]
    public void Topmost_beats_newer_and_fronted_windows_and_orders_by_activation_inside()
    {
        var pinA = new WindowStackKey(false, true, 0, WindowCategory.HUD, false, 2);
        var pinB = new WindowStackKey(false, true, 0, WindowCategory.Tools, false, 3);
        Assert.True(Above(pinA, Tools(act: 100)));
        Assert.True(Above(pinA, Hud(act: 100, lifted: true)));
        Assert.True(Above(pinB, pinA));
    }

    [Fact]
    public void Popups_beat_topmost_and_ZOrder_beats_layer()
    {
        var popup = new WindowStackKey(true, false, 0, WindowCategory.HUD, false, 1);
        var pin = new WindowStackKey(false, true, 0, WindowCategory.Tools, false, 99);
        Assert.True(Above(popup, pin));
        var hudHigh = new WindowStackKey(false, false, 5, WindowCategory.HUD, false, 1);
        Assert.True(Above(hudHigh, Tools(act: 99)));
    }

    [Fact]
    public void Sorting_gives_the_expected_bottom_to_top_order()
    {
        var keys = new List<(string Id, WindowStackKey K)>
        {
            ("pin", new WindowStackKey(false, true, 0, WindowCategory.Tools, false, 1)),
            ("hud", Hud(act: 9)),
            ("launcher", Tools(act: 4)),
            ("plugin", Tools(act: 6)),
            ("popup", new WindowStackKey(true, false, 0, WindowCategory.Tools, false, 2)),
        };
        keys.Sort((a, b) => WindowStacking.Compare(a.K, b.K));
        Assert.Equal(new[] { "hud", "launcher", "plugin", "pin", "popup" }, keys.Select(k => k.Id).ToArray());
    }
}
