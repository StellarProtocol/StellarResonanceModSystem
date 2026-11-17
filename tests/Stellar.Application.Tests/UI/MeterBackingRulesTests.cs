using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

// MeterRowElement.BackingOpacity (fw 2.33.0): opt-in, and a bad plugin value must never strip a row's backing.
public sealed class MeterBackingRulesTests
{
    [Fact]
    public void BackingOpacity_defaults_to_null()
        => Assert.Null(new MeterRowElement(() => default(MeterRowData)).BackingOpacity);

    [Theory]
    [InlineData(0.6f, 0.6f)]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(-0.2f, 0f)]   // clamped low
    [InlineData(2f, 1f)]      // clamped high
    public void Resolve_clamps_to_unit_range(float raw, float expected)
        => Assert.Equal(expected, MeterBackingRules.Resolve(() => raw));

    [Fact]
    public void Resolve_treats_NaN_as_full_backing()
        => Assert.Equal(1f, MeterBackingRules.Resolve(() => float.NaN));

    [Fact]
    public void Resolve_treats_a_throwing_delegate_as_full_backing()
        => Assert.Equal(1f, MeterBackingRules.Resolve(() => throw new InvalidOperationException()));
}
