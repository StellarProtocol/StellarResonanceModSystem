using Stellar.Infrastructure.Unity;
using Xunit;

namespace Stellar.Application.Tests.Ui;

/// <summary>Regression (owner MAIN, 2026-10-01): a CLOSED Photo Studio panel's colour picker kept taking presses —
/// windows close by SetActive(false), and the picker drag-area hit test was the one hit test without an
/// activeInHierarchy guard. Clicking the game's loot-dialog Confirm, which sat under the hidden picker, wrote a dark red
/// colour filter into the player's pinned look on every chest. Never weaken.</summary>
public sealed class PointerHitRuleTests
{
    [Fact]
    public void HiddenWindowAreaNeverTakesThePress() =>
        Assert.False(PointerHitRule.AcceptsPress(alive: true, activeInHierarchy: false, containsPointer: true, frontWindowBlocks: () => false));

    [Fact]
    public void VisibleAreaUnderThePointerTakesThePress() =>
        Assert.True(PointerHitRule.AcceptsPress(alive: true, activeInHierarchy: true, containsPointer: true, frontWindowBlocks: () => false));

    /// <summary>Regression (2026-11-08): the window-drag handle hit test was the remaining one without the guard. A hidden
    /// (parked) Tools window keeps its handle registered at its last geometry; a press on a visible HUD window's title bar
    /// over it grabbed the invisible window, so the visible one would not move. The handle test now goes through this rule.</summary>
    [Fact]
    public void ParkedWindowHandleNeverTakesTheDrag() =>
        Assert.False(PointerHitRule.AcceptsPress(alive: true, activeInHierarchy: false, containsPointer: true, frontWindowBlocks: () => false));

    [Fact]
    public void DestroyedOrMissedOrCoveredAreaDoesNot()
    {
        Assert.False(PointerHitRule.AcceptsPress(alive: false, activeInHierarchy: true, containsPointer: true, frontWindowBlocks: () => false));
        Assert.False(PointerHitRule.AcceptsPress(alive: true, activeInHierarchy: true, containsPointer: false, frontWindowBlocks: () => false));
        Assert.False(PointerHitRule.AcceptsPress(alive: true, activeInHierarchy: true, containsPointer: true, frontWindowBlocks: () => true));
    }

    /// <summary>Regression (2026-11-08): a slider scrolled out of a list's viewport is masked, not hidden, and its drag
    /// exclusion zone ignored the mask — so it blocked window drags over the area below the list. The scrollbar/slider
    /// exclusion now folds the clip test into containsPointer (contains AND NOT OutsideClip) before this rule.</summary>
    [Fact]
    public void ClippedOutSliderDoesNotClaimThePress()
    {
        const bool onBar = true, outsideClip = true;   // under the slider rect, but past the viewport mask
        Assert.False(PointerHitRule.AcceptsPress(alive: true, activeInHierarchy: true, containsPointer: onBar && !outsideClip, frontWindowBlocks: () => false));
    }

    /// <summary>Regression (2026-11-08): a visible Passive (click-through) HUD in front — RaidManager's wide mechanic
    /// alert banner — counted as covering, so windows behind it could not be dragged or hovered. In play it lets the
    /// pointer through; in layout edit mode it is an ordinary editable window and still covers.</summary>
    [Fact]
    public void PassiveFrontWindowCoversOnlyInLayoutEditMode()
    {
        Assert.False(PointerHitRule.FrontWindowCovers(passive: true, layoutEditing: false));
        Assert.True(PointerHitRule.FrontWindowCovers(passive: true, layoutEditing: true));
        Assert.True(PointerHitRule.FrontWindowCovers(passive: false, layoutEditing: false));
        Assert.True(PointerHitRule.FrontWindowCovers(passive: false, layoutEditing: true));
    }
}
