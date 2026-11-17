using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

// Rounded meter-row top line: the spec drops before the name is cut, and the name never exceeds the space left.
public sealed class MeterTopLineFitTests
{
    private const float Gap = 4f;

    [Fact]
    public void Both_fit_keeps_spec_and_full_name()
        => Assert.Equal((true, 80f), MeterTopLineFit.Decide(150f, 80f, true, 60f, Gap));

    [Fact]
    public void Exact_fit_keeps_spec()
        => Assert.Equal((true, 80f), MeterTopLineFit.Decide(144f, 80f, true, 60f, Gap));

    [Fact]
    public void Spec_drops_first_when_name_alone_fits()
        => Assert.Equal((false, 80f), MeterTopLineFit.Decide(143f, 80f, true, 60f, Gap));

    [Fact]
    public void Name_is_cut_to_the_space_left_after_spec_dropped()
        => Assert.Equal((false, 50f), MeterTopLineFit.Decide(50.7f, 80f, true, 60f, Gap));

    [Fact]
    public void No_spec_wanted_name_fits()
        => Assert.Equal((false, 80f), MeterTopLineFit.Decide(500f, 80f, false, 0f, Gap));

    [Fact]
    public void Negative_space_clamps_to_zero()
        => Assert.Equal((false, 0f), MeterTopLineFit.Decide(-12f, 80f, true, 60f, Gap));
}
