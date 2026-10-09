using System;
using System.Linq;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Application.Tests.Theme;
using Xunit;

namespace Stellar.Application.Tests.Launcher;

public sealed class LauncherRegistryTests
{
    private static LauncherRegistry NewRegistry(out InMemoryConfigSection config, out StubLog log)
    {
        config = new InMemoryConfigSection();
        log = new StubLog();
        return new LauncherRegistry(new LauncherPrefs(config), log);
    }

    private static LauncherEntry Entry(string title) =>
        new(title, IconPng: null, IconKey: null, OnOpen: () => { });

    [Fact]
    public void Register_AddsEntry_InOrder()
    {
        var reg = NewRegistry(out _, out _);
        reg.Register(Entry("A"));
        reg.Register(Entry("B"));
        reg.Register(Entry("C"));

        Assert.Equal(new[] { "A", "B", "C" }, reg.Entries.Select(e => e.Title));
    }

    [Fact]
    public void Dispose_RemovesEntry()
    {
        var reg = NewRegistry(out _, out _);
        var handle = reg.Register(Entry("A"));
        reg.Register(Entry("B"));

        handle.Dispose();

        Assert.Equal(new[] { "B" }, reg.Entries.Select(e => e.Title));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var reg = NewRegistry(out _, out _);
        var handle = reg.Register(Entry("A"));
        reg.Register(Entry("A")); // same title registered twice

        handle.Dispose();
        handle.Dispose(); // must not remove the second registration

        Assert.Single(reg.Entries);
    }

    [Fact]
    public void Mode_DefaultsToFull()
    {
        var reg = NewRegistry(out _, out _);
        Assert.Equal(LauncherMode.Full, reg.Mode);
    }

    [Fact]
    public void Mode_Persists_AcrossInstances()
    {
        var reg = NewRegistry(out var config, out _);
        reg.Mode = LauncherMode.Minimal;

        var reloaded = new LauncherRegistry(new LauncherPrefs(config), new StubLog());
        Assert.Equal(LauncherMode.Minimal, reloaded.Mode);
    }

    [Fact]
    public void Pin_Persists_AcrossInstances()
    {
        var reg = NewRegistry(out var config, out _);
        var entry = Entry("Module Optimizer");
        reg.Register(entry);

        Assert.False(reg.IsPinned(entry));
        reg.SetPinned(entry, true);
        Assert.True(reg.IsPinned(entry));

        var reloaded = new LauncherRegistry(new LauncherPrefs(config), new StubLog());
        Assert.True(reloaded.IsPinned(entry));
    }

    [Fact]
    public void Unpin_Persists()
    {
        var reg = NewRegistry(out var config, out _);
        var entry = Entry("X");
        reg.SetPinned(entry, true);
        reg.SetPinned(entry, false);

        var reloaded = new LauncherRegistry(new LauncherPrefs(config), new StubLog());
        Assert.False(reloaded.IsPinned(entry));
    }

    // ---- pin migration on Register (translated-Title -> fixed-Title+TitleProvider, owner-approved train) ----

    private static LauncherEntry EntryWithProvider(string title, string displayTitle) =>
        new(title, IconPng: null, IconKey: null, OnOpen: () => { }) { TitleProvider = () => displayTitle };

    [Fact]
    public void Register_MigratesStalePinFromOldTranslatedTitle_ToTheNewFixedTitle()
    {
        var reg = NewRegistry(out var config, out _);
        // Simulate a pin saved by last version of the plugin, which had no TitleProvider and passed the
        // translated string directly as Title.
        reg.SetPinned(Entry("컴뱃미터"), true);

        // This version ships a fixed English Title + a TitleProvider for display.
        var entry = EntryWithProvider("CombatMeter", "컴뱃미터");
        reg.Register(entry);

        Assert.True(reg.IsPinned(entry));
        Assert.False(reg.IsPinned(Entry("컴뱃미터")));

        var reloaded = new LauncherRegistry(new LauncherPrefs(config), new StubLog());
        Assert.True(reloaded.IsPinned(entry));   // migration was actually persisted, not just in-memory
    }

    [Fact]
    public void Register_DoesNotTouchPin_WhenAlreadyUnderCurrentTitle()
    {
        var reg = NewRegistry(out _, out var log);
        var entry = EntryWithProvider("CombatMeter", "컴뱃미터");
        reg.SetPinned(Entry("CombatMeter"), true);

        reg.Register(entry);

        Assert.True(reg.IsPinned(entry));
        Assert.Empty(log.WarningLines);
    }

    [Fact]
    public void Register_DoesNotMigrate_WhenNoTitleProvider()
    {
        var reg = NewRegistry(out var config, out _);
        reg.SetPinned(Entry("Module Optimizer"), true);
        var savesBeforeRegister = config.SaveCallCount; // SetPinned above legitimately saved once

        reg.Register(Entry("Module Optimizer"));   // DisplayTitle == Title (no TitleProvider) — nothing to migrate

        Assert.True(reg.IsPinned(Entry("Module Optimizer")));
        Assert.Equal(savesBeforeRegister, config.SaveCallCount);   // steady state: Register itself writes nothing
    }

    [Fact]
    public void Register_SteadyState_PerformsNoSave()
    {
        // No TitleProvider at all, nothing pinned, nothing stale — the common case for every entry on every
        // boot once a plugin has either always shipped a fixed Title or already migrated once.
        var reg = NewRegistry(out var config, out _);

        reg.Register(Entry("Module Optimizer"));

        Assert.Equal(0, config.SaveCallCount);
    }

    [Fact]
    public void Register_ThrowingTitleProvider_StillRegisters_SkipsMigration_LogsOnce()
    {
        var reg = NewRegistry(out var config, out var log);
        reg.SetPinned(Entry("컴뱃미터"), true); // a stale pin that WOULD have migrated, if the provider didn't throw
        var savesBeforeRegister = config.SaveCallCount;

        var entry = new LauncherEntry("CombatMeter", IconPng: null, IconKey: null, OnOpen: () => { })
            { TitleProvider = () => throw new InvalidOperationException("boom") };

        // Must not throw out of Register — a throwing plugin TitleProvider must never abort plugin load.
        var handle = reg.Register(entry);

        Assert.NotNull(handle);
        Assert.Contains(entry, reg.Entries);
        Assert.False(reg.IsPinned(entry));             // migration was skipped — pin stays where it was
        Assert.True(reg.IsPinned(Entry("컴뱃미터")));   // the stale pin is untouched, not lost
        Assert.Equal(savesBeforeRegister, config.SaveCallCount);   // nothing was (or could be) persisted
        Assert.Single(log.WarningLines);
        Assert.Contains("CombatMeter", log.WarningLines[0]);
    }

    [Fact]
    public void Register_Collision_SkipsMigration_WhenDisplayTitleIsAnotherEntrysTitle()
    {
        var reg = NewRegistry(out var config, out _);
        // "B" already registered under its own fixed Title "컴뱃미터" (coincidentally the same string another
        // plugin's OLD translated title used to be) — A's migration must not steal or touch B's identity.
        reg.Register(Entry("컴뱃미터"));
        reg.SetPinned(Entry("컴뱃미터"), true); // this is "B"'s own real pin, not a stale leftover
        var savesBeforeRegister = config.SaveCallCount;

        var a = EntryWithProvider("CombatMeter", "컴뱃미터");
        reg.Register(a);

        Assert.False(reg.IsPinned(a));                  // A did not steal B's pin
        Assert.True(reg.IsPinned(Entry("컴뱃미터")));     // B's pin is untouched
        Assert.Equal(savesBeforeRegister, config.SaveCallCount);
    }

    [Fact]
    public void Revision_BumpsOnContentChange_NotOnReads()
    {
        var reg = NewRegistry(out _, out _);
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
