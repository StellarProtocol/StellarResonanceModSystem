using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Stellar.Application.Tests.Config;
using Xunit;

namespace Stellar.Application.Tests.Services;

// Developer mode as plugins see it (2.30.0): IFramework.DeveloperMode + DeveloperModeChanged, read-only, relayed per
// plugin by PerPluginFramework and dropped when the plugin is disposed.
public sealed class DeveloperModePluginApiTests
{
    private static (DeveloperModePrefs prefs, FrameworkService shared, List<string> warnings) Rig()
    {
        var cfg = new PluginConfigService(new StubConfigStore(), "stellar.framework");
        var prefs = new DeveloperModePrefs(cfg.GetSection(DeveloperModePrefs.SectionName));
        var warnings = new List<string>();
        var shared = new FrameworkService { Warn = warnings.Add };
        shared.BindDeveloperMode(prefs);
        return (prefs, shared, warnings);
    }

    [Fact]
    public void Unbound_ReadsOff()
    {
        Assert.False(new FrameworkService().DeveloperMode);
    }

    [Fact]
    public void PluginView_ReadsTheFlagLive_AndGetsEachChange()
    {
        var (prefs, shared, _) = Rig();
        IFramework plugin = new PerPluginFramework("p.one", new TickScheduler(), shared);
        var seen = new List<bool>();
        plugin.DeveloperModeChanged += seen.Add;

        Assert.False(plugin.DeveloperMode);
        prefs.Enabled = true;
        Assert.True(plugin.DeveloperMode);
        prefs.Enabled = false;

        Assert.Equal(new[] { true, false }, seen);
    }

    [Fact]
    public void Unregister_DropsThePluginsHandlers()
    {
        var (prefs, shared, _) = Rig();
        var plugin = new PerPluginFramework("p.one", new TickScheduler(), shared);
        var calls = 0;
        plugin.DeveloperModeChanged += _ => calls++;

        plugin.Unregister();
        prefs.Enabled = true;

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Unregister_ReleasesTheSharedSubscription()
    {
        var (_, shared, _) = Rig();
        var plugin = new PerPluginFramework("p.one", new TickScheduler(), shared);
        Assert.Equal(1, SharedSubscribers(shared));

        plugin.Unregister();

        Assert.Equal(0, SharedSubscribers(shared));
    }

    [Fact]
    public void AThrowingPlugin_IsLoggedWithItsGuid_AndOthersStillRun()
    {
        var (prefs, shared, warnings) = Rig();
        var bad = new PerPluginFramework("p.bad", new TickScheduler(), shared);
        var good = new PerPluginFramework("p.good", new TickScheduler(), shared);
        var badSecond = 0;
        var goodCalls = 0;
        bad.DeveloperModeChanged += _ => throw new InvalidOperationException("boom");
        bad.DeveloperModeChanged += _ => badSecond++;
        good.DeveloperModeChanged += _ => goodCalls++;

        prefs.Enabled = true;

        Assert.Equal(1, badSecond);   // the bad plugin's other handler still ran
        Assert.Equal(1, goodCalls);   // and so did the other plugin
        var w = Assert.Single(warnings);
        Assert.Contains("p.bad", w);
        Assert.Contains("boom", w);
    }

    [Fact]
    public void ExternalReload_ReachesPlugins()
    {
        var store = new StubConfigStore();
        var cfg = new PluginConfigService(store, "stellar.framework");
        var prefs = new DeveloperModePrefs(cfg.GetSection(DeveloperModePrefs.SectionName));
        cfg.SectionChanged += name => { if (name == DeveloperModePrefs.SectionName) prefs.Reload(); };
        var shared = new FrameworkService();
        shared.BindDeveloperMode(prefs);
        var plugin = new PerPluginFramework("p.one", new TickScheduler(), shared);
        var seen = new List<bool>();
        plugin.DeveloperModeChanged += seen.Add;

        store.RaiseExternalChange("stellar.framework",
            new System.Text.Json.Nodes.JsonObject { ["developer"] = new System.Text.Json.Nodes.JsonObject { ["developerMode"] = true } });
        prefs.Reload();   // no-op when SectionChanged already applied it

        Assert.Equal(new[] { true }, seen);
        Assert.True(plugin.DeveloperMode);
    }

    [Fact]
    public void IFramework_StaysWithinTheMemberCap_AndTheNewFacetIsReadOnly()
    {
        Assert.True(ContractMembers.Count(typeof(IFramework)) <= 8);
        var prop = typeof(IFrameworkDeveloperMode).GetProperty(nameof(IFrameworkDeveloperMode.DeveloperMode))!;
        Assert.False(prop.CanWrite);
        Assert.True(typeof(IFrameworkDeveloperMode).IsAssignableFrom(typeof(IFramework)));
    }

    private static int SharedSubscribers(FrameworkService shared)
    {
        var field = typeof(FrameworkService).GetField(nameof(FrameworkService.DeveloperModeChanged),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (field!.GetValue(shared) as Delegate)?.GetInvocationList().Length ?? 0;
    }
}
