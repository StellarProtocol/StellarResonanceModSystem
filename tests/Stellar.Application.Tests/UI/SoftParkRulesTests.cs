using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

/// <summary>Soft park (2026-11-09): closing the Settings hub deactivated its root, and the OnDisable cascade over every
/// kept-alive tab cost ~29 ms (and the next open re-enabled and re-warmed them). A window with its own canvas and
/// KeepAlive branches now hides with its root active (canvas off, off-screen). These pin who soft-parks, what a show
/// from each state does, and the background refresh cadence.</summary>
public sealed class SoftParkRulesTests
{
    [Fact]
    public void Soft_parks_only_an_active_own_canvas_window_with_keepalive_branches_that_is_not_a_popup()
    {
        Assert.True(SoftParkRules.CanSoftPark(rootActive: true, hasOwnCanvas: true, keepAlives: 9, popup: false));
        Assert.False(SoftParkRules.CanSoftPark(rootActive: false, hasOwnCanvas: true, keepAlives: 9, popup: false));
        Assert.False(SoftParkRules.CanSoftPark(rootActive: true, hasOwnCanvas: false, keepAlives: 9, popup: false));
        Assert.False(SoftParkRules.CanSoftPark(rootActive: true, hasOwnCanvas: true, keepAlives: 0, popup: false));
        Assert.False(SoftParkRules.CanSoftPark(rootActive: true, hasOwnCanvas: true, keepAlives: 9, popup: true));
    }

    [Fact]
    public void Show_from_a_soft_park_only_unparks_no_reshow_work_and_no_layout_reset()
    {
        var p = SoftParkRules.PlanShow(softParked: true, rootActive: true, hideAll: false);
        Assert.False(p.StaySoftParked);
        Assert.True(p.WasHidden);
        Assert.True(p.SoftUnpark);
        Assert.False(p.PrepareReshow);
        Assert.False(p.ResetLayout);
    }

    [Fact]
    public void Gate_hidden_while_soft_parked_stays_parked_instead_of_deactivating()
    {
        var p = SoftParkRules.PlanShow(softParked: true, rootActive: true, hideAll: true);
        Assert.True(p.StaySoftParked);
        Assert.False(p.SoftUnpark);
        Assert.False(p.PrepareReshow);
    }

    [Fact]
    public void Show_from_a_deactivated_root_keeps_the_old_reshow_path()
    {
        var p = SoftParkRules.PlanShow(softParked: false, rootActive: false, hideAll: false);
        Assert.True(p.WasHidden);
        Assert.False(p.SoftUnpark);
        Assert.True(p.PrepareReshow);
        Assert.True(p.ResetLayout);
    }

    [Fact]
    public void An_already_shown_window_is_not_a_reshow()
    {
        var p = SoftParkRules.PlanShow(softParked: false, rootActive: true, hideAll: false);
        Assert.False(p.WasHidden);
        Assert.False(p.PrepareReshow);
        Assert.False(p.ResetLayout);
        var hide = SoftParkRules.PlanShow(softParked: false, rootActive: true, hideAll: true);
        Assert.False(hide.StaySoftParked);
        Assert.False(hide.PrepareReshow);
    }

    [Fact]
    public void Refresh_never_on_the_park_tick_then_every_nth_or_at_once_with_pending_work()
    {
        Assert.False(SoftParkRules.ShouldRefresh(0, pendingWork: true));
        Assert.True(SoftParkRules.ShouldRefresh(1, pendingWork: true));
        Assert.False(SoftParkRules.ShouldRefresh(1, pendingWork: false));
        Assert.False(SoftParkRules.ShouldRefresh(SoftParkRules.RefreshEveryTicks - 1, pendingWork: false));
        Assert.True(SoftParkRules.ShouldRefresh(SoftParkRules.RefreshEveryTicks, pendingWork: false));
        Assert.True(SoftParkRules.ShouldRefresh(SoftParkRules.RefreshEveryTicks * 2, pendingWork: false));
    }
}
