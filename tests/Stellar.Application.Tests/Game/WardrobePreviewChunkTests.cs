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
    // The weapon skin is NOT a FashionWear piece. 2.6.0 dressed it with the display-override attr, copying
    // the game's weapon-skin tab — and the owner's in-game test that same day showed the preview keeping the
    // LIVE weapon for every outfit ("for previewer it always show what user's currently using"). All three
    // override call sites dress the CACHED PLAYER model (GetCachePlayerModel), never a social-data model,
    // so the skin is now injected into the SOURCE — socialData.professionData.weaponSkin, the two-field
    // {profession_id, weapon_skin} block the server fills for SocialDataTypeWeapon — before the model is
    // generated. The plugin only puts 731 in the map when the stored skin belongs to the CURRENT class.
    // Never weaken: the injection must precede GenModelByLuaSocialData or the model is built with the
    // live skin and the async weapon load wins the race.
    [Fact]
    public void DressChunk_WeaponSkin_IsInjectedIntoTheSocialDataBeforeTheModelIsGenerated()
    {
        var outfit = new Dictionary<int, int> { [701] = 333, [WardrobeRegions.WeaponSkinPreview] = 7310003 };

        var chunk = PandaWardrobePreviewProbe.BuildModelChunk(1, outfit, dyes: null);

        Assert.Contains("local skin=7310003", chunk);
        Assert.Contains("pd.weaponSkin = skin", chunk);
        Assert.Contains("local pd = socialData.professionData", chunk);
        Assert.DoesNotContain("wd.SlotID=731", chunk);        // 731 is never a SingleWearData piece
        Assert.Equal(1, CountOf(chunk, "zList:Add(wd)"));     // only the 701 suit is dressed
        // the re-skin happens BEFORE the model exists…
        Assert.True(chunk.IndexOf("pd.weaponSkin = skin", StringComparison.Ordinal)
                    < chunk.IndexOf("GenModelByLuaSocialData", StringComparison.Ordinal));
        // …and the belt-and-braces display override still sits AFTER the wear list is committed
        Assert.Contains("m:SetLuaIntAttr((Z.ModelAttr).EModelDisplayWeaponSkinId, skin)", chunk);
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
        Assert.DoesNotContain("pd.weaponSkin = skin", chunk);
        // …but the outcome global is still armed, so the log says the key was absent rather than nothing
        Assert.Contains("rawset(_G, '__stellar_wardrobe_preview_weapon', 'none')", chunk);
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
        Assert.Contains("pd.weaponSkin = skin", chunk);
        Assert.DoesNotContain("zList:Add(wd)", chunk);   // a bare weapon key dresses no fashion piece
    }

    // A silent pcall is why 2.6.0's failure was undiagnosable from the owner's log (diagnostics=OFF, zero
    // '[WardrobePreview.lua] err' lines, no way to tell whether key 731 even arrived). Every weapon path
    // now records an outcome the host logs unconditionally: 'none', 'social=set(...)',
    // 'social=no-professionData', or an err: with the Lua message. Never weaken.
    [Fact]
    public void DressChunk_WeaponSkin_ReportsItsOutcomeThroughTheWeaponGlobal()
    {
        var outfit = new Dictionary<int, int> { [WardrobeRegions.WeaponSkinPreview] = 7320012 };

        var chunk = PandaWardrobePreviewProbe.BuildModelChunk(1, outfit, dyes: null);

        Assert.Contains("rawset(_G, '__stellar_wardrobe_preview_weapon', 'none')", chunk);   // armed first
        Assert.Contains("' social=no-professionData'", chunk);
        Assert.Contains("' social=set(prof=' .. tostring(pd.professionId) .. ',was=' .. tostring(pd.weaponSkin) .. ')'", chunk);
        Assert.Contains("' social=err:' .. tostring(werr)", chunk);
        Assert.Contains("' attr=err:' .. tostring(aerr)", chunk);
        Assert.Contains("logError('[WardrobePreview.lua] weapon skin source err: '", chunk);
        Assert.Contains("logError('[WardrobePreview.lua] weapon skin attr err: '", chunk);
        // both weapon pcalls CAPTURE their result — a bare pcall(...) is what hid the 2.6.0 failure
        Assert.Contains("local wok, werr = pcall(function()", chunk);
        Assert.Contains("local aok, aerr = pcall(function() m:SetLuaIntAttr", chunk);
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
