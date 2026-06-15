using Stellar.Abstractions.Domain.Inventory;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Inventory;

public class SelfGearCacheTests
{
    internal static GearInstance Gear(int slot, long uuid, int configId) =>
        new GearInstance(slot, uuid, configId, Quality: 5, RefineLevel: 2,
            new GearPerfection(4000, 5000, 3), GearAttrRolls.Empty, Enchant: null);

    [Fact]
    public void Current_Empty_BeforeFirstSync()
    {
        var cache = new SelfGearCache();

        Assert.NotNull(cache.Current);
        Assert.Empty(cache.Current);
    }

    [Fact]
    public void Current_Populated_AfterSinkCall()
    {
        var cache = new SelfGearCache();

        cache.OnGearSync(new[] { Gear(200, 11, 990001), Gear(201, 12, 990002) });

        Assert.Equal(2, cache.Current.Count);
        Assert.Equal(200, cache.Current[0].Slot);
        Assert.Equal(12L, cache.Current[1].ItemUuid);
    }

    [Fact]
    public void Current_Replaced_NotMerged_OnSecondSync()
    {
        var cache = new SelfGearCache();
        cache.OnGearSync(new[] { Gear(200, 11, 990001), Gear(201, 12, 990002) });

        cache.OnGearSync(new[] { Gear(205, 33, 990005) });

        var only = Assert.Single(cache.Current);
        Assert.Equal(205, only.Slot);
        Assert.Equal(33L, only.ItemUuid);
    }

    [Fact]
    public void NullSync_CoercesToEmpty()
    {
        var cache = new SelfGearCache();
        cache.OnGearSync(new[] { Gear(200, 11, 990001) });

        cache.OnGearSync(null!);

        Assert.Empty(cache.Current);
    }

    // Design decision (review-banked): full syncs are AUTHORITATIVE. An empty (non-null) sync
    // legitimately means "nothing equipped" and must wipe previous gear — the game's own client
    // does a full ResetData on every method-21, and a keep-stale guard would pin ghost gear.
    [Fact]
    public void EmptySync_WipesPreviousGear()
    {
        var cache = new SelfGearCache();
        cache.OnGearSync(new[] { Gear(200, 11, 990001) });

        cache.OnGearSync(System.Array.Empty<GearInstance>());

        Assert.Empty(cache.Current);
    }
}
