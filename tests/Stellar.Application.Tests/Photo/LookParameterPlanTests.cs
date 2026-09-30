using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Rendering;
using Xunit;
namespace Stellar.Application.Tests.Photo;

public sealed class LookParameterPlanTests
{
    [Fact]
    public void Null_groups_write_nothing() => Assert.Empty(LookParameterPlan.Build(new LookSettings()));

    [Fact]
    public void Color_maps_to_color_adjustment_fields()
    {
        var w = LookParameterPlan.Build(new LookSettings { Color = new ColorLook { PostExposure = 0.5f, Contrast = 10, Saturation = -30 } });
        Assert.Contains(new ParamWrite("Bokura.Rendering.ZColorAdjustmentVolume", "postExposure", 0.5f), w);
        Assert.Contains(new ParamWrite("Bokura.Rendering.ZColorAdjustmentVolume", "contrast", 10f), w);
        Assert.Contains(new ParamWrite("Bokura.Rendering.ZColorAdjustmentVolume", "saturation", -30f), w);
        Assert.Contains(w, x => x.Field == "colorFilter" && x.Value is RgbColor);
    }

    [Fact]
    public void Enabled_flag_is_set_for_components_that_have_one()
    {
        var w = LookParameterPlan.Build(new LookSettings { WhiteBalance = new WhiteBalanceLook { Temperature = 20 }, FilmGrain = new FilmGrainLook() });
        Assert.Contains(new ParamWrite("Bokura.Rendering.ZWhiteBalanceVolume", "Enabled", true), w);
        Assert.Contains(new ParamWrite("Bokura.Rendering.ZFilmGrainVolume", "Enabled", true), w);
    }

    [Fact]
    public void Dof_uses_the_recon_component()
    {
        var w = LookParameterPlan.Build(new LookSettings { Dof = new DofLook { FocusDistance = 2, Aperture = 1.4f, FocalLength = 85 } });
        Assert.All(w, x => Assert.Equal(LookParameterPlan.DofComponent, x.Component));
        Assert.Contains(w, x => x.Value is float f && f == 2f);
    }

    [Fact]
    public void Dof_forces_bokeh_blur_type() =>
        Assert.Contains(new ParamWrite(LookParameterPlan.DofComponent, "blurType", new EnumName("Bokeh")),
            LookParameterPlan.Build(new LookSettings { Dof = new DofLook() }));

    [Fact]
    public void Bloom_writes_ue_fields_and_keeps_component_active()
    {
        var w = LookParameterPlan.Build(new LookSettings { Bloom = new BloomLook { Intensity = 0f, Threshold = 1.2f } });
        Assert.Contains(new ParamWrite("Bokura.Rendering.ZBloomVolume", "intensity_UE", 0f), w);
        Assert.Contains(new ParamWrite("Bokura.Rendering.ZBloomVolume", "threshold_UE", 1.2f), w);
        Assert.Contains(new ParamWrite("Bokura.Rendering.ZBloomVolume", "intensity", 0.01f), w);
    }

    [Fact]
    public void Lut_carries_the_path_for_the_applier_to_load()
    {
        var w = LookParameterPlan.Build(new LookSettings { Lut = new LutLook { FilePath = "/x/lut.png", Contribution = 0.6f } });
        Assert.Contains(new ParamWrite("Bokura.Rendering.ZColorLookupVolume", "texture", new LutPath("/x/lut.png")), w);
        Assert.Contains(new ParamWrite("Bokura.Rendering.ZColorLookupVolume", "contribution", 0.6f), w);
    }

    [Theory]
    [InlineData(256, 16, true)]
    [InlineData(1024, 32, true)]
    [InlineData(512, 16, false)]
    [InlineData(16, 256, false)]
    public void Lut_strip_sizes_are_limited_to_the_two_standard_strips(int w, int h, bool ok) =>
        Assert.Equal(ok, LookParameterPlan.IsLutSize(w, h));
}
