using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Rendering;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// Photo Studio fw fix round (perf review): the look backend writes only what changed between two applies and
// drops overrides only for a group that turned off — a slider drag no longer re-writes every field of every group.
public sealed class LookParameterPlanDiffTests
{
    private static readonly LookSettings Base = new()
    {
        Color = new ColorLook { Saturation = -20 },
        Vignette = new VignetteLook(),
        Dof = new DofLook { FocusDistance = 3f },
    };

    [Fact]
    public void Identical_settings_write_nothing_and_clear_nothing() =>
        Assert.True(LookParameterPlan.Diff(Base, Base with { }).IsEmpty);

    [Fact]
    public void First_apply_writes_every_field_and_clears_nothing()
    {
        var d = LookParameterPlan.Diff(null, Base);
        Assert.Equal(LookParameterPlan.Build(Base), d.Writes);
        Assert.Empty(d.ClearComponents);
    }

    [Fact]
    public void One_changed_field_writes_only_that_field()
    {
        var next = Base with { Color = Base.Color! with { Saturation = -35 } };
        var d = LookParameterPlan.Diff(Base, next);
        Assert.Equal(new[] { new ParamWrite(LookParameterPlan.ColorComponent, "saturation", -35f) }, d.Writes);
        Assert.Empty(d.ClearComponents);
    }

    [Fact]
    public void A_group_turning_off_clears_only_that_component()
    {
        var d = LookParameterPlan.Diff(Base, Base with { Vignette = null });
        Assert.Equal(new[] { LookParameterPlan.VignetteComponent }, d.ClearComponents);
        Assert.Empty(d.Writes);
    }

    [Fact]
    public void A_group_turning_on_writes_all_its_fields_without_touching_the_others()
    {
        var next = Base with { Bloom = new BloomLook() };
        var d = LookParameterPlan.Diff(Base, next);
        Assert.All(d.Writes, w => Assert.Equal(LookParameterPlan.BloomComponent, w.Component));
        Assert.Equal(LookParameterPlan.Build(new LookSettings { Bloom = new BloomLook() }).Count, d.Writes.Count);
        Assert.Empty(d.ClearComponents);
    }

    [Fact]
    public void Everything_off_clears_every_previous_component()
    {
        var d = LookParameterPlan.Diff(Base, new LookSettings());
        Assert.Equal(
            new[] { LookParameterPlan.ColorComponent, LookParameterPlan.VignetteComponent, LookParameterPlan.DofComponent }.OrderBy(x => x),
            d.ClearComponents.OrderBy(x => x));
        Assert.Empty(d.Writes);
    }

    [Fact]
    public void Colour_and_marker_values_compare_by_value()
    {
        var a = new LookSettings { Color = new ColorLook { Filter = new RgbColor(1, 0.5f, 0.2f) }, Lut = new LutLook { FilePath = "/x.png" } };
        var b = new LookSettings { Color = new ColorLook { Filter = new RgbColor(1, 0.5f, 0.2f) }, Lut = new LutLook { FilePath = "/x.png" } };
        Assert.True(LookParameterPlan.Diff(a, b).IsEmpty);
    }

    [Fact]
    public void A_component_that_lost_a_field_is_cleared_and_fully_rewritten()
    {
        var prev = new[] { new ParamWrite("C", "a", 1f), new ParamWrite("C", "b", 2f) };
        var next = new[] { new ParamWrite("C", "a", 1f) };
        var d = LookParameterPlan.Diff(prev, next);
        Assert.Equal(new[] { "C" }, d.ClearComponents);
        Assert.Equal(next, d.Writes);
    }
}
