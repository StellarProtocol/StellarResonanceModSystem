using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

/// <summary>LayerStackElement input blocking (fw 2.33.0): which overlay rects cover a cell for the ticker's geometric
/// hit-tests. Keys stand in for native pointers, strings for the overlay rects.</summary>
public sealed class LayerStackRulesTests
{
    [Fact]
    public void BaseIsCoveredByEveryBlockingOverlay() =>
        Assert.Equal(new[] { 0, 2 }, LayerStackRules.BlockersAbove(new[] { true, false, true }, 0));

    [Fact]
    public void OverlayIsCoveredOnlyByBlockingOverlaysDrawnAfterIt()
    {
        var blocks = new[] { true, false, true };
        Assert.Equal(new[] { 2 }, LayerStackRules.BlockersAbove(blocks, 1));   // overlay 0 (layer 1)
        Assert.Equal(new[] { 2 }, LayerStackRules.BlockersAbove(blocks, 2));   // overlay 1 (layer 2)
        Assert.Empty(LayerStackRules.BlockersAbove(blocks, 3));                // the top overlay: nothing above it
    }

    [Fact]
    public void NoBlockingOverlayMeansNoCover() =>
        Assert.Empty(LayerStackRules.BlockersAbove(new[] { false, false }, 0));

    private static LayerHostInfo<int, string> Root(int stack, params string[] above) =>
        new() { Stack = stack, IsOverlay = false, Above = above };

    private static LayerHostInfo<int, string> Overlay(int stack, bool inert, params string[] above) =>
        new() { Stack = stack, IsOverlay = true, Inert = inert, Above = above };

    [Fact]
    public void CellInTheBaseGetsTheRootsBlockers()
    {
        var w = new LayerCoverWalk<int, string>();
        w.Visit(Root(1, "L1"));
        Assert.Equal(new[] { "L1" }, w.Blockers);
        Assert.False(w.Inert);
    }

    [Fact]
    public void CellInAnOverlayIsNotBlockedByItselfThroughTheStackRoot()
    {
        // overlay wrapper (nothing above it) → … → its own stack root (which lists that same overlay as a blocker)
        var w = new LayerCoverWalk<int, string>();
        w.Visit(Overlay(1, inert: false));
        w.Visit(Root(1, "L1"));
        Assert.Null(w.Blockers);
        Assert.False(w.Inert);
    }

    [Fact]
    public void CellInALowerOverlayIsBlockedByAHigherOne()
    {
        var w = new LayerCoverWalk<int, string>();
        w.Visit(Overlay(1, inert: true, "L2"));
        w.Visit(Root(1, "L1", "L2"));
        Assert.Equal(new[] { "L2" }, w.Blockers);
        Assert.True(w.Inert);   // inside a click-through layer: never interactive anyway
    }

    [Fact]
    public void AnOuterStackStillCoversAnInnerStacksOverlay()
    {
        // inner stack 2 sits in the BASE of outer stack 1; the cell is in inner overlay L2a.
        var w = new LayerCoverWalk<int, string>();
        w.Visit(Overlay(2, inert: false));
        w.Visit(Root(2, "L2a"));          // skipped (own stack)
        w.Visit(Root(1, "L1a"));          // outer overlay above the outer base → still covers
        Assert.Equal(new[] { "L1a" }, w.Blockers);
    }

    [Fact]
    public void InnerStackInsideAnOuterOverlaySkipsOnlyTheOuterRootOnce()
    {
        // inner stack 2 is CONTENT of outer overlay (stack 1); the cell is in inner stack 2's base.
        var w = new LayerCoverWalk<int, string>();
        w.Visit(Root(2, "L2a"));                  // inner base → inner overlay covers
        w.Visit(Overlay(1, inert: false, "L1b")); // the outer overlay it lives in → higher outer overlay covers
        w.Visit(Root(1, "L1a", "L1b"));           // outer root: skipped (the cell is in an overlay, not the base)
        Assert.Equal(new[] { "L2a", "L1b" }, w.Blockers);
    }
}
