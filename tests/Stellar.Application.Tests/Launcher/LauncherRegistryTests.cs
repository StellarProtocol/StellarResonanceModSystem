using System;
using System.Linq;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Application.Tests.Theme;
using Xunit;

namespace Stellar.Application.Tests.Launcher;

public sealed class LauncherRegistryTests
{
    private static LauncherRegistry NewRegistry(out InMemoryConfigSection config)
    {
        config = new InMemoryConfigSection();
        return new LauncherRegistry(new LauncherPrefs(config));
    }

    private static LauncherEntry Entry(string title) =>
        new(title, IconPng: null, IconKey: null, OnOpen: () => { });

    [Fact]
    public void Register_AddsEntry_InOrder()
    {
        var reg = NewRegistry(out _);
        reg.Register(Entry("A"));
        reg.Register(Entry("B"));
        reg.Register(Entry("C"));

        Assert.Equal(new[] { "A", "B", "C" }, reg.Entries.Select(e => e.Title));
    }

    [Fact]
    public void Dispose_RemovesEntry()
    {
        var reg = NewRegistry(out _);
        var handle = reg.Register(Entry("A"));
        reg.Register(Entry("B"));

        handle.Dispose();

        Assert.Equal(new[] { "B" }, reg.Entries.Select(e => e.Title));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var reg = NewRegistry(out _);
        var handle = reg.Register(Entry("A"));
        reg.Register(Entry("A")); // same title registered twice

        handle.Dispose();
        handle.Dispose(); // must not remove the second registration

        Assert.Single(reg.Entries);
    }

    [Fact]
    public void Mode_DefaultsToFull()
    {
        var reg = NewRegistry(out _);
        Assert.Equal(LauncherMode.Full, reg.Mode);
    }

    [Fact]
    public void Mode_Persists_AcrossInstances()
    {
        var reg = NewRegistry(out var config);
        reg.Mode = LauncherMode.Minimal;

        var reloaded = new LauncherRegistry(new LauncherPrefs(config));
        Assert.Equal(LauncherMode.Minimal, reloaded.Mode);
    }

    [Fact]
    public void Pin_Persists_AcrossInstances()
    {
        var reg = NewRegistry(out var config);
        var entry = Entry("Module Optimizer");
        reg.Register(entry);

        Assert.False(reg.IsPinned(entry));
        reg.SetPinned(entry, true);
        Assert.True(reg.IsPinned(entry));

        var reloaded = new LauncherRegistry(new LauncherPrefs(config));
        Assert.True(reloaded.IsPinned(entry));
    }

    [Fact]
    public void Unpin_Persists()
    {
        var reg = NewRegistry(out var config);
        var entry = Entry("X");
        reg.SetPinned(entry, true);
        reg.SetPinned(entry, false);

        var reloaded = new LauncherRegistry(new LauncherPrefs(config));
        Assert.False(reloaded.IsPinned(entry));
    }

    [Fact]
    public void Revision_BumpsOnContentChange_NotOnReads()
    {
        var reg = NewRegistry(out _);
        var entry = Entry("A");

        int afterRegister1 = reg.Revision;
        var handle = reg.Register(entry);
        Assert.True(reg.Revision > afterRegister1);   // Register bumps

        int afterRegister2 = reg.Revision;
        reg.SetPinned(entry, true);
        Assert.True(reg.Revision > afterRegister2);    // pin toggle bumps

        // Reads don't bump — the launcher caches against this counter.
        int afterPin = reg.Revision;
        _ = reg.IsPinned(entry);
        _ = reg.Entries.Count;
        Assert.Equal(afterPin, reg.Revision);

        handle.Dispose();
        Assert.True(reg.Revision > afterPin);          // removal bumps

        // Idempotent dispose must not bump again (nothing removed).
        int afterRemove = reg.Revision;
        handle.Dispose();
        Assert.Equal(afterRemove, reg.Revision);
    }
}
