using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Pins the wardrobe 3D-preview dress chunk (<see cref="PandaWardrobePreviewProbe.BuildModelChunk"/>).
/// Regression origin: Discord "Wardrobe Plugin enhancements" thread (2026-09-03/04) — with two head
/// accessories worn (713 Headwear + 718 HeadWearSecond) the preview showed head slot 1 for a moment and
/// then head slot 2 replaced it. Root cause: every <c>SingleWearData</c> was built with <c>SlotID=0</c>,
/// while the game's own <c>fashion_vm.GetFashionWearList</c> sets <c>SlotId = region</c> and the model
/// routes a head piece to its mount (HeadWear vs HeadWear2) by that SlotID — so both landed on ONE mount.
/// The dress chunk must stamp each piece's SlotID with its FashionRegion. Never weaken.
/// </summary>
public sealed class WardrobePreviewChunkTests
{
    [Fact]
    public void DressChunk_StampsEachPieceWithItsRegionAsSlotID()
    {
        var outfit = new Dictionary<int, int> { [713] = 111, [718] = 222, [701] = 333, [702] = 0 };

        var chunk = PandaWardrobePreviewProbe.BuildModelChunk(42, outfit, dyes: null);

        Assert.Contains("wd.FashionID=111 wd.SlotID=713", chunk);   // Headwear (head slot 1)
        Assert.Contains("wd.FashionID=222 wd.SlotID=718", chunk);   // HeadWearSecond (head slot 2)
        Assert.Contains("wd.FashionID=333 wd.SlotID=701", chunk);
        Assert.DoesNotContain("wd.SlotID=0", chunk);       // the pre-fix shape that collapsed both head slots
        Assert.DoesNotContain("wd.FashionID=0 ", chunk);   // empty regions are not dressed
    }

    [Fact]
    public void DressChunk_TwoHeadPieces_ProduceTwoWearEntriesOnOneList()
    {
        var outfit = new Dictionary<int, int> { [713] = 111, [718] = 222 };

        var chunk = PandaWardrobePreviewProbe.BuildModelChunk(1, outfit, dyes: null);

        Assert.Equal(2, CountOf(chunk, "zList:Add(wd)"));
        Assert.Contains("m:SetLuaAttr((Z.LocalAttr).EWearFashion, zList)", chunk);
    }

    [Fact]
    public void DressChunk_PerAreaDyes_StillPlaceColoursByArea_WithTheRegionSlot()
    {
        var outfit = new Dictionary<int, int> { [718] = 222 };
        var dyes = new Dictionary<int, IReadOnlyDictionary<int, float[]>>
        {
            [718] = new Dictionary<int, float[]> { [3] = new[] { 0.5f, 0.25f, 0.75f } },
        };

        var chunk = PandaWardrobePreviewProbe.BuildModelChunk(1, outfit, dyes);

        Assert.Contains("wd.FashionID=222 wd.SlotID=718", chunk);
        Assert.Contains("[3]=(Vector3.New)(0.5,0.25,0.75)", chunk);
        Assert.Contains("for a=0,16 do bc:Add(dye[a] or Vector3.zero) end", chunk);
    }

    // Owner, 2026-09-05: "The 3D preview does not show the weapon skin. <-- it should show if class match".
    // The weapon skin is NOT a FashionWear piece; the game dresses a preview model through the display
    // override attr (fashion_weapon_skin_select_view.SelectStyle:26). The plugin only puts 731 in the map
    // when the stored skin belongs to the player's CURRENT class.
    [Fact]
    public void DressChunk_WeaponSkin_UsesTheDisplayOverrideAttr_AndNeverTheWearList()
    {
        var outfit = new Dictionary<int, int> { [701] = 333, [WardrobeRegions.WeaponSkinPreview] = 7310003 };

        var chunk = PandaWardrobePreviewProbe.BuildModelChunk(1, outfit, dyes: null);

        Assert.Contains("m:SetLuaIntAttr((Z.ModelAttr).EModelDisplayWeaponSkinId, skin)", chunk);
        Assert.Contains("local skin=7310003", chunk);
        Assert.DoesNotContain("wd.SlotID=731", chunk);        // 731 is never a SingleWearData piece
        Assert.Equal(1, CountOf(chunk, "zList:Add(wd)"));     // only the 701 suit is dressed
        // the weapon call sits AFTER the wear list is committed
        Assert.True(chunk.IndexOf("EWearFashion", StringComparison.Ordinal)
                    < chunk.IndexOf("EModelDisplayWeaponSkinId", StringComparison.Ordinal));
    }

    [Fact]
    public void DressChunk_WithoutAWeaponSkin_LeavesTheModelsOwnWeaponAlone()
    {
        var outfit = new Dictionary<int, int> { [701] = 333, [713] = 111 };

        var chunk = PandaWardrobePreviewProbe.BuildModelChunk(1, outfit, dyes: null);

        Assert.DoesNotContain("EModelDisplayWeaponSkinId", chunk);
        Assert.DoesNotContain("GetWeaponOriginSkinId", chunk);
    }

    // Skin 0 = "the class's default look". The apply path resolves it via GetWeaponOriginSkinId
    // (weapon_skill_skin_vm.AsyncUseProfessionSkin:199), so the preview resolves it the same way — otherwise
    // hovering an outfit saved on the default look would keep showing the skin the player is wearing NOW.
    [Fact]
    public void DressChunk_WeaponSkinZero_ResolvesTheDefaultLookTheSameWayApplyingDoes()
    {
        var outfit = new Dictionary<int, int> { [WardrobeRegions.WeaponSkinPreview] = 0 };

        var chunk = PandaWardrobePreviewProbe.BuildModelChunk(1, outfit, dyes: null);

        Assert.Contains("local skin=0", chunk);
        Assert.Contains("skin = svm:GetWeaponOriginSkinId((wvm.GetCurWeapon)())", chunk);
        Assert.Contains("m:SetLuaIntAttr((Z.ModelAttr).EModelDisplayWeaponSkinId, skin)", chunk);
        Assert.DoesNotContain("zList:Add(wd)", chunk);   // a bare weapon key dresses no fashion piece
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
