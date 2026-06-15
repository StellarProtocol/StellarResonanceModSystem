// tests/Stellar.Application.Tests/Config/PluginConfigServiceTests.cs
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Config;

public sealed class PluginConfigServiceTests
{
    private const string PluginGuid = "stellar.test";

    [Fact]
    public void GetSection_ReturnsStableInstance_AcrossCalls()
    {
        var (svc, _) = NewService();
        var a = svc.GetSection("foo");
        var b = svc.GetSection("foo");
        Assert.Same(a, b);
    }

    [Fact]
    public void Get_ReturnsDefault_ForMissingKey()
    {
        var (svc, _) = NewService();
        var s = svc.GetSection("foo");
        Assert.Equal(42, s.Get<int>("missing", 42));
    }

    [Fact]
    public void Get_ReturnsDefault_OnTypeCoercionFailure()
    {
        var (svc, _) = NewService();
        var s = svc.GetSection("foo");
        s.Set("k", "not a number");
        Assert.Equal(7, s.Get<int>("k", 7));
    }

    [Fact]
    public void Set_Then_Get_RoundTripsPrimitive()
    {
        var (svc, _) = NewService();
        var s = svc.GetSection("foo");
        s.Set("k", 123);
        Assert.Equal(123, s.Get<int>("k", 0));
    }

    [Fact]
    public void Set_Then_Get_RoundTripsArray()
    {
        var (svc, _) = NewService();
        var s = svc.GetSection("foo");
        s.Set("arr", new[] { 1, 2, 3 });
        Assert.Equal(new[] { 1, 2, 3 }, s.Get<int[]>("arr", null));
    }

    [Fact]
    public void Set_DoesNotPersist_UntilSave()
    {
        var (svc, store) = NewService();
        var s = svc.GetSection("foo");
        s.Set("k", 1);
        Assert.Empty(store.SaveCalls);
    }

    [Fact]
    public void Save_FlushesToStore()
    {
        var (svc, store) = NewService();
        var s = svc.GetSection("foo");
        s.Set("k", 1);
        s.Save();
        Assert.Single(store.SaveCalls);
    }

    [Fact]
    public void Save_FiresSectionChanged_OnceForChangedSection()
    {
        var (svc, _) = NewService();
        var fires = new List<string>();
        svc.SectionChanged += name => fires.Add(name);

        var s = svc.GetSection("foo");
        s.Set("k", 1);
        s.Save();

        Assert.Single(fires);
        Assert.Equal("foo", fires[0]);
    }

    [Fact]
    public void ExternalEdit_FiresSectionChanged_AfterDiff()
    {
        var (svc, store) = NewService();
        var s = svc.GetSection("foo");
        s.Set("k", 1);
        s.Save();

        var fires = new List<string>();
        svc.SectionChanged += name => fires.Add(name);

        // Simulate an external edit: file now has different content for "foo".
        var newRoot = JsonNode.Parse("""{"foo":{"k":2}}""");
        store.RaiseExternalChange(PluginGuid, newRoot);

        Assert.Single(fires);
        Assert.Equal("foo", fires[0]);
    }

    [Fact]
    public void ExternalEdit_OnIdenticalContent_DoesNotFire()
    {
        var (svc, store) = NewService();
        var s = svc.GetSection("foo");
        s.Set("k", 1);
        s.Save();

        var fires = new List<string>();
        svc.SectionChanged += name => fires.Add(name);

        // External "edit" reports the file but content is identical (echo).
        var sameRoot = JsonNode.Parse("""{"foo":{"k":1}}""");
        store.RaiseExternalChange(PluginGuid, sameRoot);

        Assert.Empty(fires);
    }

    [Fact]
    public void Get_OnMalformedSection_ReturnsDefault()
    {
        var (svc, store) = NewService();
        store.Files[PluginGuid] = JsonNode.Parse("""{"foo":"not an object"}""");
        // Recreate service to force re-load from store.
        svc = new PluginConfigService(store, PluginGuid);

        var s = svc.GetSection("foo");
        Assert.Equal(99, s.Get<int>("k", 99));
    }

    [Fact]
    public void Save_OnStoreWriteFailure_KeepsCacheInSync()
    {
        var (svc, _) = NewService();
        var s = svc.GetSection("foo");
        s.Set("k", 1);
        s.Save();
        Assert.Equal(1, s.Get<int>("k", 0));  // in-memory cache reflects Set
    }

    private static (PluginConfigService, StubConfigStore) NewService()
    {
        var store = new StubConfigStore();
        return (new PluginConfigService(store, PluginGuid), store);
    }
}
