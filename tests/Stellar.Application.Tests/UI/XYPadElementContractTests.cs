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

    // The XY pad ships in framework 2.18.0; the exact current-version pin moved to UiLanguagesTests (2.21.0).
    [Fact]
    public void Framework_version_is_at_least_2_18_0()
        => Assert.True(System.Version.Parse(FrameworkVersion.Value) >= new System.Version(2, 18, 0), FrameworkVersion.Value);
}
