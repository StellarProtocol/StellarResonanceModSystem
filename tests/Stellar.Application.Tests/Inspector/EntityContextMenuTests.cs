using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Inspector;

public sealed class EntityContextMenuTests
{
    private static EntityId E(long c) => new EntityId((c << 16) | 640L);

    [Fact]
    public void Register_ItemAppearsForEntity_AndOnClickBindsEntity()
    {
        var svc = new EntityContextMenuService();
        EntityId clicked = default;
        svc.Register("Inspect", null, id => clicked = id);
        var items = svc.ItemsFor(E(5));
        Assert.Single(items);
        Assert.Equal("Inspect", items[0].Label);
        items[0].OnClick();
        Assert.Equal(E(5), clicked);
    }

    [Fact]
    public void IsVisible_GatesPerEntity()
    {
        var svc = new EntityContextMenuService();
        svc.Register("OnlyFive", id => (id.Value >> 16) == 5, _ => { });
        Assert.Single(svc.ItemsFor(E(5)));
        Assert.Empty(svc.ItemsFor(E(6)));
    }

    [Fact]
    public void Dispose_Unregisters()
    {
        var svc = new EntityContextMenuService();
        var token = svc.Register("X", null, _ => { });
        Assert.Single(svc.ItemsFor(E(1)));
        token.Dispose();
        Assert.Empty(svc.ItemsFor(E(1)));
    }
}
