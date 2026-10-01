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

    [Fact]
    public void DestroyedOrMissedOrCoveredAreaDoesNot()
    {
        Assert.False(PointerHitRule.AcceptsPress(alive: false, activeInHierarchy: true, containsPointer: true, frontWindowBlocks: () => false));
        Assert.False(PointerHitRule.AcceptsPress(alive: true, activeInHierarchy: true, containsPointer: false, frontWindowBlocks: () => false));
        Assert.False(PointerHitRule.AcceptsPress(alive: true, activeInHierarchy: true, containsPointer: true, frontWindowBlocks: () => true));
    }
}
