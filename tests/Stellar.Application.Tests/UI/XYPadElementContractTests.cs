using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.Application.Tests.UI;

public sealed class XYPadElementContractTests
{
    [Fact]
    public void Defaults_are_a_minus_one_to_one_pad_with_an_8_division_grid_filling_its_cell()
    {
        var pad = new XYPadElement(() => (0f, 0f), (_, _) => { });
        Assert.Equal(-1f, pad.Min);
        Assert.Equal(1f, pad.Max);
        Assert.Null(pad.Enabled);
        Assert.Equal(0f, pad.Size);
        Assert.Equal(8, pad.GridLines);
        Assert.IsAssignableFrom<HudElement>(pad);
    }

    // The XY pad ships in framework 2.18.0; the exact current-version pin follows the latest release (2.19.1).
    [Fact]
    public void Framework_version_is_2_19_1() => Assert.Equal("2.19.1", FrameworkVersion.Value);
}
