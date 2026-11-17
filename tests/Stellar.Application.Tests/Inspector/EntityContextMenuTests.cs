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
    public void Register_WithIcon_SurfacesSameArray_AndNoIconIsNull()
    {
        var svc = new EntityContextMenuService();
        var png = new byte[] { 1, 2, 3 };
        svc.Register("Plain", null, _ => { });
        svc.Register("Iconic", null, _ => { }, png);
        var items = svc.ItemsFor(E(1));
        Assert.Equal(2, items.Count);
        Assert.Null(items[0].IconPng);
        Assert.Same(png, items[1].IconPng);   // the registered reference, so renderers can cache by reference
    }

    [Fact]
    public void Register_WithIcon_ThroughInterface_DispatchesToService()
    {
        // Calls the interface overload (a default interface method) — the service's own method must win, not the
        // icon-dropping default body.
        Stellar.Abstractions.Services.IEntityContextMenu svc = new EntityContextMenuService();
        var png = new byte[] { 9 };
        svc.Register("Iconic", null, _ => { }, png);
        Assert.Same(png, svc.ItemsFor(E(1))[0].IconPng);
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
