using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

// Rounded meter-row top line: drop order is spec → class → cut the name, and the name never exceeds the space left.
public sealed class MeterTopLineFitTests
{
    private const float Gap = 4f;

    private static (bool, bool, float) Fit(float avail, float name, bool clsOn, float cls, bool specOn, float spec)
        => MeterTopLineFit.Decide(avail, name, new(clsOn, cls), new(specOn, spec), Gap);

    // ── spec only (class not wanted) ──
    [Fact]
    public void Both_fit_keeps_spec_and_full_name()
        => Assert.Equal((false, true, 80f), Fit(150f, 80f, false, 0f, true, 60f));

    [Fact]
    public void Exact_fit_keeps_spec()
        => Assert.Equal((false, true, 80f), Fit(144f, 80f, false, 0f, true, 60f));

    [Fact]
    public void Spec_drops_first_when_name_alone_fits()
        => Assert.Equal((false, false, 80f), Fit(143f, 80f, false, 0f, true, 60f));

    [Fact]
    public void Name_is_cut_to_the_space_left_after_spec_dropped()
        => Assert.Equal((false, false, 50f), Fit(50.7f, 80f, false, 0f, true, 60f));

    [Fact]
    public void No_label_wanted_name_fits()
        => Assert.Equal((false, false, 80f), Fit(500f, 80f, false, 0f, false, 0f));

    [Fact]
    public void Negative_space_clamps_to_zero()
        => Assert.Equal((false, false, 0f), Fit(-12f, 80f, true, 40f, true, 60f));

    // ── class + spec ("Both"): name 80, class 40, spec 60 → all = 80+4+40+4+60 = 188; name+class = 124 ──
    [Fact]
    public void Class_and_spec_both_fit()
        => Assert.Equal((true, true, 80f), Fit(188f, 80f, true, 40f, true, 60f));

    [Fact]
    public void Spec_drops_before_class()
        => Assert.Equal((true, false, 80f), Fit(187f, 80f, true, 40f, true, 60f));

    [Fact]
    public void Class_kept_at_exact_fit_after_spec_dropped()
        => Assert.Equal((true, false, 80f), Fit(124f, 80f, true, 40f, true, 60f));

    [Fact]
    public void Class_drops_before_name_is_cut()
        => Assert.Equal((false, false, 80f), Fit(123f, 80f, true, 40f, true, 60f));

    [Fact]
    public void Name_cut_only_after_class_and_spec_dropped()
        => Assert.Equal((false, false, 70f), Fit(70.4f, 80f, true, 40f, true, 60f));

    // ── class only (spec not wanted) ──
    [Fact]
    public void Class_only_fits()
        => Assert.Equal((true, false, 80f), Fit(124f, 80f, true, 40f, false, 0f));

    [Fact]
    public void Class_only_drops_then_name_fits()
        => Assert.Equal((false, false, 80f), Fit(100f, 80f, true, 40f, false, 0f));

    [Fact]
    public void Unwanted_labels_never_reported_shown()
        => Assert.Equal((false, false, 80f), Fit(1000f, 80f, false, 40f, false, 60f));
}
