using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

/// <summary>StackLayer.OnEscape (fw 2.35.0): which shown overlay gets an Esc press — frontmost window, then its top
/// overlay; an open confirm in that window or one in front takes the press instead.</summary>
public sealed class EscapeLayerRulesTests
{
    private const int NoConfirm = int.MinValue;

    [Fact]
    public void NoEligibleLayerPicksNothing() =>
        Assert.Equal(-1, LayerStackRules.PickEscapeLayer(System.Array.Empty<(int, long)>(), NoConfirm));

    [Fact]
    public void FrontmostWindowWinsOverAnyLaterRegistration() =>
        // window 5 (front) registered first, window 2 (back) registered later: still the front one.
        Assert.Equal(0, LayerStackRules.PickEscapeLayer(new[] { (5, 1L), (2, 9L) }, NoConfirm));

    [Fact]
    public void InOneWindowTheTopOverlayWins() =>
        // same window: the later registration is drawn above (a nested stack inside an overlay, or a later overlay).
        Assert.Equal(2, LayerStackRules.PickEscapeLayer(new[] { (3, 4L), (3, 2L), (3, 7L) }, NoConfirm));

    [Fact]
    public void ConfirmInTheSameWindowTakesThePress() =>
        Assert.Equal(-1, LayerStackRules.PickEscapeLayer(new[] { (3, 4L) }, frontConfirmZ: 3));

    [Fact]
    public void ConfirmInAFrontWindowTakesThePress() =>
        Assert.Equal(-1, LayerStackRules.PickEscapeLayer(new[] { (3, 4L) }, frontConfirmZ: 6));

    [Fact]
    public void ConfirmInABackWindowDoesNotStopTheFrontLayer() =>
        Assert.Equal(1, LayerStackRules.PickEscapeLayer(new[] { (1, 1L), (4, 2L) }, frontConfirmZ: 2));
}
