using System;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Inventory;

public class InventoryServiceTests
{
    [Fact]
    public void IsAvailable_False_BeforeFirstSuccessfulRefresh()
    {
        var probe = new StubInventoryProbe();
        var svc = new InventoryService(probe, new SelfGearCache(), new StubLog());

        Assert.False(svc.IsAvailable);
        Assert.Null(svc.GetModules());
        Assert.Null(svc.GetEquipped());
    }

    [Fact]
    public void Refresh_SetsAvailable_AndFiresEvent_OnFirstSample()
    {
        var probe = new StubInventoryProbe
        {
            NextModules = StubInventoryProbe.SnapshotOf((1, 100)),
            NextEquipped = StubInventoryProbe.EquippedOf((1, 1)),
        };
        var svc = new InventoryService(probe, new SelfGearCache(), new StubLog());
        var fires = 0;
        svc.InventoryChanged += () => fires++;

        svc.Refresh();

        Assert.True(svc.IsAvailable);
        Assert.NotNull(svc.GetModules());
        Assert.NotNull(svc.GetEquipped());
        Assert.Equal(1, fires);
    }

    [Fact]
    public void Refresh_SuppressesEvent_WhenSnapshotUnchanged()
    {
        var probe = new StubInventoryProbe
        {
            NextModules = StubInventoryProbe.SnapshotOf((1, 100)),
            NextEquipped = StubInventoryProbe.EquippedOf((1, 1)),
        };
        var svc = new InventoryService(probe, new SelfGearCache(), new StubLog());
        var fires = 0;
        svc.InventoryChanged += () => fires++;

        svc.Refresh();
        svc.Refresh();
        svc.Refresh();

        Assert.Equal(1, fires);
    }

    [Fact]
    public void Refresh_FiresEvent_OnEquippedSlotChange()
    {
        var probe = new StubInventoryProbe
        {
            NextModules = StubInventoryProbe.SnapshotOf((1, 100), (2, 100)),
            NextEquipped = StubInventoryProbe.EquippedOf((1, 1)),
        };
        var svc = new InventoryService(probe, new SelfGearCache(), new StubLog());
        var fires = 0;
        svc.InventoryChanged += () => fires++;

        svc.Refresh();

        probe.NextEquipped = StubInventoryProbe.EquippedOf((1, 2));
        svc.Refresh();

        Assert.Equal(2, fires);
    }

    [Fact]
    public void Refresh_FiresEvent_OnNewModuleAppearing()
    {
        var probe = new StubInventoryProbe
        {
            NextModules = StubInventoryProbe.SnapshotOf((1, 100)),
            NextEquipped = StubInventoryProbe.EquippedOf(),
        };
        var svc = new InventoryService(probe, new SelfGearCache(), new StubLog());
        var fires = 0;
        svc.InventoryChanged += () => fires++;

        svc.Refresh();

        probe.NextModules = StubInventoryProbe.SnapshotOf((1, 100), (2, 101));
        svc.Refresh();

        Assert.Equal(2, fires);
    }

    [Fact]
    public void Refresh_NoCrash_WhenProbeUnreadable()
    {
        var probe = new StubInventoryProbe { ModulesReadable = false };
        var svc = new InventoryService(probe, new SelfGearCache(), new StubLog());
        var fires = 0;
        svc.InventoryChanged += () => fires++;

        svc.Refresh();

        Assert.False(svc.IsAvailable);
        Assert.Equal(0, fires);
    }

    [Fact]
    public void GetSelfGear_Empty_BeforeFirstSync_ThenServesCache()
    {
        var cache = new SelfGearCache();
        var svc = new InventoryService(new StubInventoryProbe(), cache, new StubLog());

        Assert.Empty(svc.GetSelfGear());

        cache.OnGearSync(new[] { SelfGearCacheTests.Gear(200, 11, 990001) });

        var only = Assert.Single(svc.GetSelfGear());
        Assert.Equal(200, only.Slot);
    }

    [Fact]
    public void SubscriberException_DoesNotAbortRefresh_OrCrash()
    {
        var probe = new StubInventoryProbe
        {
            NextModules = StubInventoryProbe.SnapshotOf((1, 100)),
            NextEquipped = StubInventoryProbe.EquippedOf(),
        };
        var svc = new InventoryService(probe, new SelfGearCache(), new StubLog());
        svc.InventoryChanged += () => throw new InvalidOperationException("subscriber boom");

        svc.Refresh();

        Assert.True(svc.IsAvailable);
    }
}
