using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Pins the outfit show/hide override on the wardrobe preview (framework 2.31.0, Wardrobe #6):
/// <c>IWardrobePreview.Show(..., hidden)</c> writes the model attr <c>Z.LocalAttr.EWearSetting</c> after dressing,
/// so a piece the player hides globally can still be previewed when the outfit shows it. Without a map the chunk
/// must stay byte-identical to 2.30.0's (no EWearSetting write at all).
/// </summary>
public sealed class WardrobePreviewHiddenTests
{
    private static readonly int[] Regions = { 701, 702, 703, 711, 712, 713, 714, 715, 716, 717, 718, 721, 722, 723 };

    [Fact]
    public void WearSetting_EmptyMap_ShowsAll14RegionsExplicitly()
    {
        var s = PandaWardrobePreviewProbe.BuildWearSetting(new Dictionary<int, bool>());

        Assert.Equal("701=1|702=1|703=1|711=1|712=1|713=1|714=1|715=1|716=1|717=1|718=1|721=1|722=1|723=1|", s);
    }

    [Fact]
    public void WearSetting_HiddenRegionsAre2_EverythingElse1_AlwaysAll14()
    {
        var s = PandaWardrobePreviewProbe.BuildWearSetting(new Dictionary<int, bool> { [713] = true, [717] = true, [701] = false });

        Assert.Equal("701=1|702=1|703=1|711=1|712=1|713=2|714=1|715=1|716=1|717=2|718=1|721=1|722=1|723=1|", s);
        var parts = s.TrimEnd('|').Split('|');
        Assert.Equal(14, parts.Length);
        for (var i = 0; i < Regions.Length; i++) Assert.StartsWith(Regions[i] + "=", parts[i]);
    }

    [Fact]
    public void WearSetting_IgnoresKeysOutsideTheFourteenRegions()
    {
        // 731 (weapon skin) is not hideable in the game's settings panel; 999 is junk — neither may reach the string
        var s = PandaWardrobePreviewProbe.BuildWearSetting(new Dictionary<int, bool> { [WardrobeRegions.WeaponSkinPreview] = true, [999] = true });

        Assert.DoesNotContain("731=", s);
        Assert.DoesNotContain("999=", s);
        Assert.Equal(14, s.TrimEnd('|').Split('|').Length);
    }

    [Fact]
    public void Chunk_WithHiddenMap_WritesEWearSettingAfterTheDress_Guarded_AndReportsTheOutcome()
    {
        var outfit = new Dictionary<int, int> { [701] = 333, [713] = 111 };

        var chunk = PandaWardrobePreviewProbe.BuildModelChunk(1, outfit, dyes: null, hidden: new Dictionary<int, bool> { [713] = true });

        Assert.Contains("local hs = '701=1|702=1|703=1|711=1|712=1|713=2|", chunk);
        Assert.Contains("local hok, herr = pcall(function() m:SetLuaAttr((Z.LocalAttr).EWearSetting, hs) end)", chunk);
        // after the dress: the game writes it on its own preview models after EWearFashion (face_change_body_view:211)
        Assert.True(chunk.IndexOf("(Z.LocalAttr).EWearFashion", StringComparison.Ordinal)
                    < chunk.IndexOf("(Z.LocalAttr).EWearSetting", StringComparison.Ordinal));
        // …and before the model is handed to C#
        Assert.True(chunk.IndexOf("(Z.LocalAttr).EWearSetting", StringComparison.Ordinal)
                    < chunk.IndexOf("rawset(_G, '__stellar_wardrobe_preview_model', m)", StringComparison.Ordinal));
        // outcome lands on the one-line weapon log so an in-game hover proves it took effect
        Assert.Contains("' hide=ok(rb=' .. hrb .. ')'", chunk);
        Assert.Contains("' hide=err:' .. tostring(herr)", chunk);
    }

    [Fact]
    public void Chunk_WithHiddenMap_AndWeaponSkin_KeepsTheMountClearAfterTheWearSetting()
    {
        var outfit = new Dictionary<int, int> { [701] = 333, [WardrobeRegions.WeaponSkinPreview] = 7320012 };

        var chunk = PandaWardrobePreviewProbe.BuildModelChunk(1, outfit, dyes: null, hidden: new Dictionary<int, bool>());

        Assert.Contains("m:SetLuaAttr((Z.ModelAttr).EModelCMountWeaponL, '')", chunk);
        Assert.True(chunk.IndexOf("(Z.LocalAttr).EWearSetting", StringComparison.Ordinal)
                    < chunk.IndexOf("EModelCMountWeaponL, ''", StringComparison.Ordinal));
    }

    // Compile-time pin: the 2.31.0 members live on a facet base (IWardrobePreviewOptions — IWardrobePreview is at the
    // STELLAR0005 8-member cap) yet must be callable straight off IWardrobePreview, and the 4-arg overload must not
    // make the existing 2-/3-arg calls ambiguous. Never invoked; it only has to compile.
    [Fact]
    public void Api_NewMembersAreReachableFromIWardrobePreview_WithoutAmbiguity()
    {
        Action<IWardrobePreview> use = p =>
        {
            var outfit = new Dictionary<int, int>();
            p.ShowWeapon = true;
            p.Show(default, outfit);
            p.Show(default, outfit, null);
            p.Show(default, outfit, null, new Dictionary<int, bool> { [713] = true });
            p.Show(default, outfit, dyes: null, hidden: null);
        };
        Assert.NotNull(use);
    }

    [Fact]
    public void Chunk_WithoutHiddenMap_IsUnchanged()
    {
        var outfit = new Dictionary<int, int> { [701] = 333, [713] = 111, [WardrobeRegions.WeaponSkinPreview] = 7320012 };

        var legacy = PandaWardrobePreviewProbe.BuildModelChunk(1, outfit, dyes: null);
        var explicitNull = PandaWardrobePreviewProbe.BuildModelChunk(1, outfit, dyes: null, hidden: null);

        Assert.Equal(legacy, explicitNull);
        Assert.DoesNotContain("EWearSetting", legacy);
        Assert.DoesNotContain("hide=", legacy);
    }
}
