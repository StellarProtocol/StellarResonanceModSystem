using System.Collections.Generic;
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

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
