using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.Application.Tests.Domain;

// Lights review I-7 (2026-10-03): a light's colour has no alpha, so Photo Studio hides the Opacity slider. Every other
// picker keeps it — the default must stay true, and the two-argument constructor must keep compiling for old plugins.
public sealed class ColorPickerElementTests
{
    [Fact]
    public void ShowAlpha_defaults_to_true_and_can_be_turned_off()
    {
        var p = new ColorPickerElement(() => new ColorRgba(1f, 1f, 1f, 1f), _ => { });
        Assert.True(p.ShowAlpha);
        Assert.False((p with { ShowAlpha = false }).ShowAlpha);
    }
}
