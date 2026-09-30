using Stellar.Application.Abstractions;
using Xunit;

namespace Stellar.Application.Tests.Photo;

// Regression: GameVisibilityBackend.Reassert used to return early for a layer whose restore (show) call had
// previously failed (singleton briefly unavailable), because the failure already cleared the layer out of
// _applied — so want == have (both "not hidden") looked like nothing to do, and the retry never happened. The
// layer stayed hidden forever. ShouldInvoke's restorePending term is the fix: it keeps the retry alive even
// though nothing is reported held.
public sealed class LayerStepDecisionTests
{
    [Fact]
    public void Nothing_to_do_when_wanted_matches_held_and_not_forced_and_nothing_pending()
    {
        Assert.False(LayerStepDecision.ShouldInvoke(want: false, have: false, force: false, restorePending: false));
        Assert.False(LayerStepDecision.ShouldInvoke(want: true, have: true, force: false, restorePending: false));
    }

    [Fact]
    public void Invokes_when_wanted_state_differs_from_held()
    {
        Assert.True(LayerStepDecision.ShouldInvoke(want: true, have: false, force: false, restorePending: false));
        Assert.True(LayerStepDecision.ShouldInvoke(want: false, have: true, force: false, restorePending: false));
    }

    [Fact]
    public void Reassert_forces_a_held_hide_to_reissue_even_though_unchanged()
    {
        Assert.True(LayerStepDecision.ShouldInvoke(want: true, have: true, force: true, restorePending: false));
    }

    [Fact]
    public void Force_alone_never_reissues_an_already_restored_layer()
    {
        // force only re-forces a HIDE (want=true); a restored layer (want=false, have=false) with nothing
        // pending must stay untouched even under Reassert's force:true.
        Assert.False(LayerStepDecision.ShouldInvoke(want: false, have: false, force: true, restorePending: false));
    }

    [Fact]
    public void A_pending_restore_retries_even_though_nothing_is_held()
    {
        // This is the exact bug shape: want=false (we want it shown), have=false (the failed attempt already
        // cleared _applied), so nothing looks held — only restorePending keeps the retry alive.
        Assert.True(LayerStepDecision.ShouldInvoke(want: false, have: false, force: false, restorePending: true));
        Assert.True(LayerStepDecision.ShouldInvoke(want: false, have: false, force: true, restorePending: true));
    }

    [Fact]
    public void A_pending_restore_never_gates_a_hide_request()
    {
        // restorePending only matters while want is false; it must not suppress or force a hide by itself
        // beyond what want/have/force already decide.
        Assert.True(LayerStepDecision.ShouldInvoke(want: true, have: false, force: false, restorePending: true));
        Assert.True(LayerStepDecision.ShouldInvoke(want: true, have: true, force: true, restorePending: true));
    }

    [Fact]
    public void A_successful_restore_clears_the_pending_bit()
    {
        Assert.False(LayerStepDecision.NextRestorePending(want: false, ok: true, wasPending: true));
        Assert.False(LayerStepDecision.NextRestorePending(want: false, ok: true, wasPending: false));
    }

    [Fact]
    public void A_failed_restore_sets_the_pending_bit()
    {
        Assert.True(LayerStepDecision.NextRestorePending(want: false, ok: false, wasPending: false));
        Assert.True(LayerStepDecision.NextRestorePending(want: false, ok: false, wasPending: true));
    }

    [Fact]
    public void A_hide_call_never_touches_the_pending_bit()
    {
        Assert.True(LayerStepDecision.NextRestorePending(want: true, ok: true, wasPending: true));
        Assert.True(LayerStepDecision.NextRestorePending(want: true, ok: false, wasPending: true));
        Assert.False(LayerStepDecision.NextRestorePending(want: true, ok: true, wasPending: false));
        Assert.False(LayerStepDecision.NextRestorePending(want: true, ok: false, wasPending: false));
    }
}
