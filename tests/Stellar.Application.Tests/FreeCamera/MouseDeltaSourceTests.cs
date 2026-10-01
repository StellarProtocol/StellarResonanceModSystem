using Stellar.Infrastructure.UI;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

/// <summary>Regression (owner MAIN, 2026-10-01): holding RMB in orbit did not rotate. The game's ZMouseManager locks the
/// cursor while RMB is held over the game view, so Input.mousePosition never moves and the position delta was always 0.
/// The raw "Mouse X"/"Mouse Y" axes report movement while the cursor is locked. Never weaken.</summary>
public sealed class MouseDeltaSourceTests
{
    [Fact]
    public void Axes_win_when_available_even_with_a_frozen_pointer()
    {
        var (x, y) = MouseDeltaSource.Pick(axisOk: true, axisX: 0.5f, axisY: 0.2f, posDx: 0f, posDy: 0f);
        Assert.Equal(0.5f * MouseDeltaSource.AxisToPixels, x, 3);
        Assert.Equal(-0.2f * MouseDeltaSource.AxisToPixels, y, 3);   // axis up = +; our delta is top-left origin (down = +)
    }

    [Fact]
    public void Position_delta_is_the_fallback_when_the_axes_are_missing()
    {
        var (x, y) = MouseDeltaSource.Pick(axisOk: false, axisX: 0f, axisY: 0f, posDx: 7f, posDy: -3f);
        Assert.Equal(7f, x);
        Assert.Equal(-3f, y);
    }
}
