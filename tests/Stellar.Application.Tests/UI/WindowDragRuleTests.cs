using Stellar.Infrastructure.Unity;
using Xunit;

namespace Stellar.Application.Tests.Ui;

/// <summary>Regression (2026-11-09): title-bar-less Draggable windows (Dungeon Hub, MultiTool) use the whole frame as the
/// drag handle, so dragging inside a scroll list moved the window instead of scrolling it, and press-and-drag on a button
/// moved the window. In play, interactive controls and lists that actually scroll own the press; everything else (header,
/// text, panel backgrounds, gaps) still drags. Layout edit mode keeps drag-from-anywhere.</summary>
public sealed class WindowDragRuleTests
{
    [Fact]
    public void PlainSurfaceStartsTheDrag() =>
        Assert.True(WindowDragRule.ShouldStart(layoutEditing: false, onInteractive: false, onOverflowingScroll: false));

    [Fact]
    public void InteractiveControlDoesNotStartTheDrag() =>
        Assert.False(WindowDragRule.ShouldStart(layoutEditing: false, onInteractive: true, onOverflowingScroll: false));

    [Fact]
    public void ScrollingListDoesNotStartTheDrag() =>
        Assert.False(WindowDragRule.ShouldStart(layoutEditing: false, onInteractive: false, onOverflowingScroll: true));

    [Fact]
    public void ButtonInsideScrollingListDoesNotStartTheDrag() =>
        Assert.False(WindowDragRule.ShouldStart(layoutEditing: false, onInteractive: true, onOverflowingScroll: true));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LayoutEditModeDragsFromAnywhere(bool onInteractive, bool onOverflowingScroll) =>
        Assert.True(WindowDragRule.ShouldStart(layoutEditing: true, onInteractive, onOverflowingScroll));

    [Fact]
    public void ContentTallerThanViewportOverflows() =>
        Assert.True(WindowDragRule.AxisOverflows(axisEnabled: true, contentSize: 400f, viewportSize: 200f));

    [Fact]
    public void ListThatFitsStaysADragSurface()
    {
        Assert.False(WindowDragRule.AxisOverflows(axisEnabled: true, contentSize: 150f, viewportSize: 200f));
        Assert.False(WindowDragRule.AxisOverflows(axisEnabled: true, contentSize: 200f, viewportSize: 200f));
        // Layout rounding: a hair over the viewport is not scrollable content.
        Assert.False(WindowDragRule.AxisOverflows(axisEnabled: true, contentSize: 200.4f, viewportSize: 200f));
    }

    [Fact]
    public void DisabledAxisNeverOverflows() =>
        Assert.False(WindowDragRule.AxisOverflows(axisEnabled: false, contentSize: 400f, viewportSize: 200f));
}
