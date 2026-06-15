using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.GameData;

public sealed class GameDataInventoryServiceTests
{
    [Fact]
    public void GetItem_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataInventoryService();
        svc.LoadItems(new Dictionary<int, ItemInfo>
        {
            [5500102] = new ItemInfo(5500102, "High-Perf Attack Mod",
                "+atk", "icons/55.png", 3, ItemKind.Module, 55001),
        });

        var result = svc.GetItem(5500102);

        Assert.NotNull(result);
        Assert.Equal(ItemKind.Module, result!.Value.Kind);
        Assert.Equal(3, result.Value.Quality);
    }

    [Fact]
    public void GetItem_ReturnsNull_WhenCacheUnset()
        => Assert.Null(new GameDataInventoryService().GetItem(1));

    [Fact]
    public void GetEquip_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataInventoryService();
        svc.LoadEquips(new Dictionary<int, EquipInfo>
        {
            [9001] = new EquipInfo(9001, "Frostweave Robe", 4, new[] { 21, 22 }),
        });

        Assert.Equal("Frostweave Robe", svc.GetEquip(9001)!.Value.Name);
    }

    [Fact]
    public void GetWeapon_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataInventoryService();
        svc.LoadWeapons(new Dictionary<int, WeaponInfo>
        {
            [42] = new WeaponInfo(42, "Frostbrand", WeaponKind.Sword, 250),
        });

        var result = svc.GetWeapon(42);

        Assert.Equal(WeaponKind.Sword, result!.Value.Kind);
        Assert.Equal(250, result.Value.BaseDamage);
    }

    [Fact]
    public void Get_ReturnsNull_ForUnknownIds()
    {
        var svc = new GameDataInventoryService();
        svc.LoadItems(new Dictionary<int, ItemInfo>());
        svc.LoadEquips(new Dictionary<int, EquipInfo>());
        svc.LoadWeapons(new Dictionary<int, WeaponInfo>());

        Assert.Null(svc.GetItem(1));
        Assert.Null(svc.GetEquip(1));
        Assert.Null(svc.GetWeapon(1));
    }
}
