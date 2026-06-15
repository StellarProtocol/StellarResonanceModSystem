using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.Application.Tests.Inspector;

public sealed class EntityDetailTests
{
    private static EntityId Player(long charId) => new EntityId((charId << 16) | 640L);

    [Fact]
    public void Attributes_RetainsFullMap()
    {
        var t = new Stellar.Application.Services.CombatEntityTracker();
        var id = Player(123);
        t.SetEntityAttribute(id, 15, 182469);
        t.SetEntityAttribute(id, 10030, 45400);
        var attrs = t.GetAttributes(id);
        Assert.Equal(182469, attrs[15]);
        Assert.Equal(45400, attrs[10030]);
    }

    [Fact]
    public void Equipment_RetainsList()
    {
        var t = new Stellar.Application.Services.CombatEntityTracker();
        var id = Player(123);
        t.SetEntityEquipment(id, new[] { new EquipNineEntry(200, 2001110), new EquipNineEntry(201, 2010942) });
        var equip = t.GetEquipment(id);
        Assert.Equal(2, equip.Count);
        Assert.Equal(2001110, equip[0].ItemId);
        Assert.Equal(200, equip[0].Slot);
    }

    [Fact]
    public void Unknown_ReturnsEmpty()
    {
        var t = new Stellar.Application.Services.CombatEntityTracker();
        Assert.Empty(t.GetAttributes(Player(999)));
        Assert.Empty(t.GetEquipment(Player(999)));
    }

    [Fact]
    public void Fashion_RoundTrips_PerEntity()
    {
        var t = new Stellar.Application.Services.CombatEntityTracker();
        var a = Player(123);
        var b = Player(456);
        t.SetEntityFashion(a, new[]
        {
            new FashionEntry(1, 5300123, new[] { new ColorRgba(230 / 255f, 20 / 255f, 64 / 255f, 1f) }),
            new FashionEntry(3, 5301555, FashionEntry.NoDyes),
        });
        var fashion = t.GetFashion(a);
        Assert.Equal(2, fashion.Count);
        Assert.Equal(1, fashion[0].Slot);
        Assert.Equal(5300123, fashion[0].FashionId);
        Assert.Single(fashion[0].Dyes);
        Assert.Equal(3, fashion[1].Slot);
        Assert.Equal(5301555, fashion[1].FashionId);
        Assert.Empty(fashion[1].Dyes);
        Assert.Empty(t.GetFashion(b));
    }

    [Fact]
    public void Fashion_Evicted_OnDisappear()
    {
        var t = new Stellar.Application.Services.CombatEntityTracker();
        var id = Player(123);
        t.SetEntityFashion(id, new[] { new FashionEntry(1, 5300123, FashionEntry.NoDyes) });
        Assert.NotEmpty(t.GetFashion(id));
        t.OnEntityDisappeared(id);
        Assert.Empty(t.GetFashion(id));
    }
}
