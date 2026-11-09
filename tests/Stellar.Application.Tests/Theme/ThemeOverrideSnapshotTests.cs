// tests/Stellar.Application.Tests/Theme/ThemeOverrideSnapshotTests.cs
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Theme;

// The "Edit colours" Save / Discard baseline: diff (unsaved count) and restore against the live override service,
// through the real ColorRegistryService + an in-memory override store (the same path the dialog writes through).
public sealed class ThemeOverrideSnapshotTests
{
    private static readonly ColorRgba Red = new(1f, 0f, 0f, 1f);
    private static readonly ColorRgba Blue = new(0f, 0f, 1f, 1f);
    private static readonly ColorRgba Grey = new(0.5f, 0.5f, 0.5f, 1f);

    private static (ColorRegistryService svc, MemoryOverrideStore store) New()
    {
        var theme = new FakeNamedTheme(ThemePreset.Default);
        theme.SetActiveCustom("Mine", ThemePreset.Default);
        var store = new MemoryOverrideStore();
        var svc = new ColorRegistryService(theme, store);
        svc.Register("Theme.MenuAccent", "Accent", Grey);
        svc.Register("Theme.Danger", "Danger", Grey);
        svc.Register("Plugin.Bar", "Bar", Grey);
        return (svc, store);
    }

    private static ThemeOverrideSnapshot Capture(ColorRegistryService svc) => ThemeOverrideSnapshot.Capture("Mine", svc, svc.Slots);

    [Fact]
    public void NoEdits_CountsZero()
    {
        var (svc, _) = New();
        svc.SetOverride("Theme.MenuAccent", Red);
        Assert.Equal(0, Capture(svc).CountChanges(svc, svc.Slots));
    }

    [Fact]
    public void ChangedValue_CountsAndRestores()
    {
        var (svc, _) = New();
        svc.SetOverride("Theme.MenuAccent", Red);
        var snap = Capture(svc);
        svc.SetOverride("Theme.MenuAccent", Blue);
        Assert.Equal(1, snap.CountChanges(svc, svc.Slots));
        Assert.Equal(1, snap.Restore(svc, svc.Slots));
        Assert.Equal(Red, svc.Resolve("Theme.MenuAccent"));
        Assert.Equal(0, snap.CountChanges(svc, svc.Slots));
    }

    [Fact]
    public void SameEightBitValue_IsNotAChange()
    {
        var (svc, _) = New();
        svc.SetOverride("Theme.MenuAccent", Red);
        var snap = Capture(svc);
        svc.SetOverride("Theme.MenuAccent", new ColorRgba(0.999f, 0.001f, 0f, 1f));   // a picker drag back to #FF0000
        Assert.Equal(0, snap.CountChanges(svc, svc.Slots));
    }

    [Fact]
    public void AddedOverride_CountsAndDiscardClearsIt()
    {
        var (svc, _) = New();
        var snap = Capture(svc);
        svc.SetOverride("Plugin.Bar", Grey);   // "+ Add colour override" seeds at the resolved colour
        Assert.Equal(1, snap.CountChanges(svc, svc.Slots));
        snap.Restore(svc, svc.Slots);
        Assert.False(svc.HasOverride("Plugin.Bar"));
    }

    [Fact]
    public void ResetToDefault_CountsAndDiscardPutsTheOverrideBack()
    {
        var (svc, _) = New();
        svc.SetOverride("Theme.Danger", Blue);
        svc.SetOverride("Plugin.Bar", Red);
        var snap = Capture(svc);
        svc.ClearOverride("Theme.Danger");   // ✕ reset-to-default
        svc.ClearOverride("Plugin.Bar");     // a removed plugin override
        Assert.Equal(2, snap.CountChanges(svc, svc.Slots));
        Assert.Equal(2, snap.Restore(svc, svc.Slots));
        Assert.True(svc.HasOverride("Theme.Danger"));
        Assert.Equal(Blue, svc.Resolve("Theme.Danger"));
        Assert.Equal(Red, svc.Resolve("Plugin.Bar"));
    }

    [Fact]
    public void Restore_TouchesOnlyDifferingSlots()
    {
        var (svc, store) = New();
        svc.SetOverride("Theme.MenuAccent", Red);
        svc.SetOverride("Theme.Danger", Blue);
        var snap = Capture(svc);
        svc.SetOverride("Theme.Danger", Red);
        Assert.Equal(1, snap.Restore(svc, svc.Slots));
        Assert.Equal(Red, svc.Resolve("Theme.MenuAccent"));
        Assert.Equal(Blue, svc.Resolve("Theme.Danger"));
        Assert.Equal(0, store.FlushCount);   // the caller persists once, not the snapshot
    }

    [Fact]
    public void SlotRegisteredAfterCapture_IsAdoptedNotCounted()
    {
        var (svc, store) = New();
        var snap = Capture(svc);
        store.Set("Mine", "Late.Slot", Red);   // stored override of a plugin that registers later
        svc.Register("Late.Slot", "Late", Grey);
        Assert.Equal(0, snap.CountChanges(svc, svc.Slots));
        Assert.Equal(0, snap.Restore(svc, svc.Slots));
        Assert.True(svc.HasOverride("Late.Slot"));
    }

    [Fact]
    public void Same_HandlesNotOverridden()
    {
        Assert.True(ThemeOverrideSnapshot.Same(null, null));
        Assert.False(ThemeOverrideSnapshot.Same(null, Red));
        Assert.False(ThemeOverrideSnapshot.Same(Red, null));
        Assert.False(ThemeOverrideSnapshot.Same(Red, Blue));
    }
}
