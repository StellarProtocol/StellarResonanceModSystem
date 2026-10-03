using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Recon run 2 F: the game's own release recipe leaves head=False where it was True; restore = snapshot + corrective
// writes. Pinned so a later "simplification" back to fixed off-values fails here.
public sealed class LookAtRestorePlanTests
{
    [Fact]
    public void Head_left_off_by_the_game_recipe_is_switched_back_on()
    {
        var w = LookAtRestorePlan.Corrections(new LookAtSnapshot(true, true, false), new LookAtSnapshot(true, false, false));
        Assert.Equal(new[] { new LookAtWrite(LookAtWriteKind.HeadClose, false) }, w);
    }

    [Fact]
    public void Eye_and_enable_drift_is_written_back()
    {
        var w = LookAtRestorePlan.Corrections(new LookAtSnapshot(false, true, false), new LookAtSnapshot(true, true, true));
        Assert.Equal(new[] { new LookAtWrite(LookAtWriteKind.EyeOpen, false), new LookAtWrite(LookAtWriteKind.Enable, false) }, w);
    }

    [Fact]
    public void Matching_state_needs_no_writes() =>
        Assert.Empty(LookAtRestorePlan.Corrections(new LookAtSnapshot(true, true, false), new LookAtSnapshot(true, true, false)));

    [Fact]
    public void Unreadable_after_state_writes_every_known_field()
    {
        var w = LookAtRestorePlan.Corrections(new LookAtSnapshot(true, true, null), null);
        Assert.Equal(new[] { new LookAtWrite(LookAtWriteKind.HeadClose, false), new LookAtWrite(LookAtWriteKind.Enable, true) }, w);
    }

    // Controller fix round 1: Restore() must not depend on a same-frame read of Head. The probe only proved the
    // corrective restore after a 1 s settle; a lazily-updated component can still report the Apply-time ("applied")
    // value for one frame after the recipe's own SetLuaAttrLookAtHeadClose(true) write, which would make Corrections
    // see a false "no drift" match and silently drop the write the player is owed.
    [Fact]
    public void AfterRelease_always_reports_head_closed_regardless_of_the_live_read()
    {
        Assert.False(LookAtRestorePlan.AfterRelease(new LookAtSnapshot(true, true, false)).Head);
        Assert.False(LookAtRestorePlan.AfterRelease(new LookAtSnapshot(false, false, true)).Head);
        Assert.False(LookAtRestorePlan.AfterRelease(null).Head);
    }

    [Fact]
    public void AfterRelease_keeps_the_live_read_for_eye_and_enable()
    {
        var after = LookAtRestorePlan.AfterRelease(new LookAtSnapshot(true, false, true));
        Assert.Equal(true, after.Enable);
        Assert.Equal(true, after.Eye);
    }

    [Fact]
    public void Head_restore_fires_even_when_a_stale_read_still_matches_pre()
    {
        // Pre = head was on. A stale same-frame read still shows "true" (the Apply-time value, not yet settled) — the
        // OLD bug: comparing pre.Head against that stale read sees no drift and skips the write. AfterRelease must
        // still force the correction through, with no dependency on what the live read says for Head.
        var staleLiveRead = new LookAtSnapshot(true, true, false);
        var after = LookAtRestorePlan.AfterRelease(staleLiveRead);
        var w = LookAtRestorePlan.Corrections(new LookAtSnapshot(true, true, false), after);
        Assert.Equal(new[] { new LookAtWrite(LookAtWriteKind.HeadClose, false) }, w);
    }
}
