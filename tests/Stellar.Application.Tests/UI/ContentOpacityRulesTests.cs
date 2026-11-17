using System;
using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

// WindowSpec.ContentOpacity (fw 2.33.0): opt-in, and a bad plugin value must never hide the window.
public sealed class ContentOpacityRulesTests
{
    [Fact]
    public void ContentOpacity_defaults_to_null()
    {
        var spec = new WindowSpec("t", "t", new WindowRect(0f, 0f, 10f, 10f), WindowCategory.HUD, WindowPanelStyle.Borderless)
        { ShouldRender = () => true };
        Assert.Null(spec.ContentOpacity);
    }

    [Theory]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1f, 1f)]
    [InlineData(0f, 0f)]
    [InlineData(-0.3f, 0f)]   // clamped low
    [InlineData(1.7f, 1f)]    // clamped high
    public void Resolve_clamps_to_unit_range(float raw, float expected)
        => Assert.Equal(expected, ContentOpacityRules.Resolve(() => raw));

    [Fact]
    public void Resolve_treats_NaN_as_fully_visible()
        => Assert.Equal(1f, ContentOpacityRules.Resolve(() => float.NaN));

    [Fact]
    public void Resolve_treats_a_throwing_delegate_as_fully_visible()
        => Assert.Equal(1f, ContentOpacityRules.Resolve(() => throw new InvalidOperationException()));
}
