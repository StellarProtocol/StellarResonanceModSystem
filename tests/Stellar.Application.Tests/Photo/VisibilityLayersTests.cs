using System;
using System.Linq;
using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.Application.Tests.Photo;

public sealed class VisibilityLayersTests
{
    [Fact]
    public void New_layers_have_the_spec_values_and_old_values_are_unchanged()
    {
        Assert.Equal(1, (int)VisibilityLayers.GameHud);
        Assert.Equal(16, (int)VisibilityLayers.KeepParty);
        Assert.Equal(32, (int)VisibilityLayers.Self);
        Assert.Equal(64, (int)VisibilityLayers.EffectsMine);
        Assert.Equal(128, (int)VisibilityLayers.EffectsParty);
        Assert.Equal(256, (int)VisibilityLayers.EffectsOthers);
        Assert.Equal(512, (int)VisibilityLayers.EffectsMonsters);
    }

    [Fact]
    public void Effects_set_is_exactly_the_four_effect_layers()
    {
        Assert.Equal(VisibilityLayers.EffectsMine | VisibilityLayers.EffectsParty | VisibilityLayers.EffectsOthers |
                     VisibilityLayers.EffectsMonsters, VisibilityLayerSets.Effects);
    }

    [Fact]
    public void Every_layer_is_a_single_bit()
    {
        foreach (var v in Enum.GetValues<VisibilityLayers>().Where(v => v != VisibilityLayers.None))
            Assert.Equal(0, (int)v & ((int)v - 1));
    }
}
