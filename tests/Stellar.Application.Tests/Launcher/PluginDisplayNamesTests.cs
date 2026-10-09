using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Stellar.Application.Tests.Theme;
using Xunit;

namespace Stellar.Application.Tests.Launcher;

// Settings shows a plugin under the DisplayTitle of the first launcher entry THAT plugin registered (its own
// localized tile title), else its internal name. Owner attribution is recorded at Register by PerPluginLauncher.
public sealed class PluginDisplayNamesTests
{
    private static LauncherRegistry NewRegistry(out StubLog log)
    {
        log = new StubLog();
        return new LauncherRegistry(new LauncherPrefs(new InMemoryConfigSection()), log);
    }

    private static LauncherEntry Entry(string title, Func<string>? provider = null) =>
        new(title, IconPng: null, IconKey: null, OnOpen: () => { }) { TitleProvider = provider };

    [Fact]
    public void Resolve_ReturnsDisplayTitleOfThePluginsFirstEntry()
    {
        var reg = NewRegistry(out var log);
        var launcher = new PerPluginLauncher("stellar.moduleoptimizer", reg, reg);
        launcher.Register(Entry("Module Optimizer", () => "모듈 최적화"));
        launcher.Register(Entry("Module Optimizer Results", () => "모듈 최적화 — 결과"));

        var names = new PluginDisplayNames(reg, log);

        Assert.Equal("모듈 최적화", names.Resolve("stellar.moduleoptimizer", "ModuleOptimizer"));
    }

    [Fact]
    public void Resolve_EntryWithoutProvider_UsesItsTitle()
    {
        var reg = NewRegistry(out var log);
        new PerPluginLauncher("p", reg, reg).Register(Entry("Photo Studio"));

        Assert.Equal("Photo Studio", new PluginDisplayNames(reg, log).Resolve("p", "Stellar.PhotoStudio"));
    }

    [Fact]
    public void Resolve_NoEntry_FallsBackToInternalName()
    {
        var reg = NewRegistry(out var log);
        Assert.Equal("Stellar.Maestro", new PluginDisplayNames(reg, log).Resolve("stellar.maestro", "Stellar.Maestro"));
        Assert.Empty(log.WarningLines);
    }

    [Fact]
    public void Resolve_ThrowingProvider_FallsBack_AndLogsOnce()
    {
        var reg = NewRegistry(out var log);
        new PerPluginLauncher("p", reg, reg).Register(Entry("T", () => throw new InvalidOperationException("boom")));
        log.WarningLines.Clear();   // Register's pin-migration check logs its own line; this test is about Resolve
        var names = new PluginDisplayNames(reg, log);

        Assert.Equal("Internal", names.Resolve("p", "Internal"));
        Assert.Equal("Internal", names.Resolve("p", "Internal"));

        var line = Assert.Single(log.WarningLines);
        Assert.Contains("'p'", line);
    }

    [Fact]
    public void Resolve_ThrowingProvider_IsNotReinvoked()
    {
        // Row Funcs re-resolve every apply; a provider that threw once must not throw (and cost) every frame.
        var reg = NewRegistry(out var log);
        var calls = 0;
        new PerPluginLauncher("p", reg, reg).Register(Entry("T", () => { calls++; throw new InvalidOperationException("boom"); }));
        calls = 0;   // Register's pin-migration check reads DisplayTitle once
        var names = new PluginDisplayNames(reg, log);

        names.Resolve("p", "Internal");
        names.Resolve("p", "Internal");
        names.Resolve("p", "Internal");

        Assert.Equal(1, calls);
    }

    [Fact]
    public void SameInstanceRegisteredTwice_DisposingOneHandle_KeepsTheOwner()
    {
        var reg = NewRegistry(out _);
        var launcher = new PerPluginLauncher("p", reg, reg);
        var e = Entry("Twice");
        var h1 = launcher.Register(e);
        launcher.Register(e);

        h1.Dispose();

        Assert.Same(e, Assert.Single(reg.Entries));
        Assert.Same(e, reg.FirstEntryOwnedBy("p"));
    }

    [Fact]
    public void Resolve_EntriesOwnedByOthers_DoNotLeak()
    {
        var reg = NewRegistry(out var log);
        reg.Register(Entry("Settings", () => "설정"));                                  // untagged (framework)
        new PerPluginLauncher("other", reg, reg).Register(Entry("Other", () => "다른"));  // another plugin

        var names = new PluginDisplayNames(reg, log);

        Assert.Equal("Mine", names.Resolve("mine", "Mine"));
        Assert.Equal("다른", names.Resolve("other", "Other"));
    }

    [Fact]
    public void Resolve_ValueEqualEntriesOfTwoPlugins_KeepTheirOwnOwner()
    {
        // LauncherEntry is a record: ownership must be per INSTANCE, and disposing one plugin's entry must not
        // remove the other plugin's value-equal one.
        var reg = NewRegistry(out var log);
        Action open = () => { };
        var a = new LauncherEntry("Same", null, null, open);
        var b = new LauncherEntry("Same", null, null, open);
        new PerPluginLauncher("a", reg, reg).Register(a);
        var handleB = new PerPluginLauncher("b", reg, reg).Register(b);

        handleB.Dispose();   // the LATER one: a value-equality List.Remove would evict `a` instead

        Assert.Null(reg.FirstEntryOwnedBy("b"));
        Assert.Same(a, reg.FirstEntryOwnedBy("a"));
        Assert.Same(a, Assert.Single(reg.Entries));
    }

    [Fact]
    public void FirstEntryOwnedBy_FollowsRegistrationOrder_AndDispose()
    {
        var reg = NewRegistry(out _);
        var launcher = new PerPluginLauncher("p", reg, reg);
        var first = Entry("First");
        var second = Entry("Second");
        var h1 = launcher.Register(first);
        launcher.Register(second);

        Assert.Same(first, reg.FirstEntryOwnedBy("p"));
        h1.Dispose();
        Assert.Same(second, reg.FirstEntryOwnedBy("p"));
    }

    [Fact]
    public void PerPluginServices_LauncherIsTheScopedOne()
    {
        var reg = NewRegistry(out _);
        var scoped = new PerPluginLauncher("p", reg, reg);
        var scope = new PerPluginScope(null!, null!, null!, Hotkeys: null, Harmony: null, Localization: null,
                                       Launcher: scoped);

        Assert.Same(scoped, new PerPluginServices(null!, scope).Launcher);
    }
}
