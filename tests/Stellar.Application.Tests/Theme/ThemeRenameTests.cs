using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Theme;

// Renaming a custom theme keeps its colours: every record keyed by the theme NAME moves with it (colour overrides,
// base preset); stale overrides already under the new name are dropped, not merged; other themes are untouched.
// One InMemoryConfigSection stands in for the framework's single config file (every section is one JSON tree).
public sealed class ThemeRenameTests
{
    private static readonly ColorRgba Red = new(1f, 0f, 0f, 1f);
    private static readonly ColorRgba Green = new(0f, 1f, 0f, 1f);
    private static readonly ColorRgba Blue = new(0f, 0f, 1f, 1f);

    private static (InMemoryConfigSection Cfg, ColorOverrideStore Ovr, CustomThemeStore Store) New()
    {
        var cfg = new InMemoryConfigSection();
        var ovr = new ColorOverrideStore(cfg);
        return (cfg, ovr, new CustomThemeStore(cfg, ovr));
    }

    [Fact]
    public void Rename_MovesOverrides_InOneWrite_AndSurvivesReload()
    {
        var (cfg, ovr, store) = New();
        store.Create("Ocean", ThemePreset.Dark);
        ovr.Set("Ocean", "Theme.accent", Red);
        ovr.Set("Ocean", "Theme.text", Green);
        ovr.Flush();
        var saves = cfg.SaveCallCount;

        store.Rename("Ocean", "Lagoon");

        Assert.Equal(saves + 1, cfg.SaveCallCount);   // theme list + overrides land in the same write
        Assert.True(ovr.TryGet("Lagoon", "Theme.accent", out var a) && a.R > 0.99f);
        Assert.True(ovr.Has("Lagoon", "Theme.text"));
        Assert.False(ovr.Has("Ocean", "Theme.accent"));   // no orphan left behind
        Assert.False(ovr.Has("Ocean", "Theme.text"));
        Assert.Equal(ThemePreset.Dark, store.BasePresetOf("Lagoon"));

        var reloaded = new ColorOverrideStore(cfg);   // what the next session reads from disk
        Assert.True(reloaded.Has("Lagoon", "Theme.accent"));
        Assert.False(reloaded.Has("Ocean", "Theme.accent"));
        Assert.Contains("Lagoon", new CustomThemeStore(cfg).Names);
    }

    [Fact]
    public void Rename_CaseOnly_IsARealMove()
    {
        var (_, ovr, store) = New();
        store.Create("Blue", ThemePreset.Default);
        ovr.Set("Blue", "Theme.accent", Blue);

        store.Rename("Blue", "blue");

        Assert.Equal(new[] { "blue" }, store.Names);
        Assert.True(ovr.Has("blue", "Theme.accent"));
        Assert.False(ovr.Has("Blue", "Theme.accent"));
    }

    [Fact]
    public void Rename_OrphanUnderTheNewName_IsReplaced_NotMerged()
    {
        var (_, ovr, store) = New();
        store.Create("Mine", ThemePreset.Default);
        ovr.Set("Mine", "Theme.accent", Red);
        // Left behind by a deleted theme once called "Gone" (Delete keeps overrides): one slot clashes, one doesn't.
        ovr.Set("Gone", "Theme.accent", Green);
        ovr.Set("Gone", "Theme.border", Green);

        store.Rename("Mine", "Gone");

        Assert.True(ovr.TryGet("Gone", "Theme.accent", out var a) && a.R > 0.99f && a.G < 0.01f);   // ours, not the orphan's
        Assert.False(ovr.Has("Gone", "Theme.border"));                                             // orphan-only slot dropped
    }

    [Fact]
    public void Rename_LeavesOtherThemesUntouched()
    {
        var (_, ovr, store) = New();
        store.Create("A", ThemePreset.Default);
        store.Create("B", ThemePreset.Crimson);
        ovr.Set("A", "Theme.accent", Red);
        ovr.Set("B", "Theme.accent", Blue);

        store.Rename("A", "C");

        Assert.True(ovr.TryGet("B", "Theme.accent", out var b) && b.B > 0.99f);
        Assert.Equal(ThemePreset.Crimson, store.BasePresetOf("B"));
        Assert.Equal(new[] { "C", "B" }, store.Names);   // order kept
    }

    [Fact]
    public void Create_DropsStaleOverridesUnderTheName()
    {
        var (_, ovr, store) = New();
        ovr.Set("Ocean_copy", "Theme.accent", Red);   // orphan of a deleted theme
        store.Create("Ocean_copy", ThemePreset.Dark);
        Assert.False(ovr.Has("Ocean_copy", "Theme.accent"));
    }

    // Window opacity is keyed by the BASE PRESET (windowOpacity.<preset>), never by the custom name, so it follows a
    // rename by itself: the base preset moves with the theme.
    [Fact]
    public void Rename_KeepsWindowOpacity_AndActiveColours_AndTheEditBaselineClean()
    {
        var (cfg, ovr, store) = New();
        var named = new NamedThemeService(cfg, new StubLog());
        var reg = new ColorRegistryService(named, ovr);
        reg.Register("Theme.accent", "Accent", Blue);
        store.Create("Ocean", ThemePreset.Light);
        named.SetActiveCustom("Ocean", ThemePreset.Light);
        reg.SetOverride("Theme.accent", Red);
        named.SetWindowOpacity(0.5f);
        var snap = ThemeOverrideSnapshot.Capture("Ocean", reg, reg.Slots);   // Edit colours dialog open

        // What ThemeEditorBody.CommitRename does: store rename, baseline follows, active pointer re-set.
        store.Rename("Ocean", "Lagoon");
        snap.Theme = "Lagoon";
        named.SetActiveCustom("Lagoon", store.BasePresetOf("Lagoon"));

        Assert.Equal("Lagoon", named.ActiveCustomName);
        Assert.True(reg.HasOverride("Theme.accent"));
        Assert.True(reg.Resolve("Theme.accent").R > 0.99f);
        Assert.Equal(0, snap.CountChanges(reg, reg.Slots));   // "No changes" — not a false unsaved edit
        Assert.Equal(0.5f, named.WindowOpacity, 3);
    }

    [Fact]
    public void Rename_WithUnsavedEdits_KeepsThemUnsaved()
    {
        var (cfg, ovr, store) = New();
        var named = new NamedThemeService(cfg, new StubLog());
        var reg = new ColorRegistryService(named, ovr);
        reg.Register("Theme.accent", "Accent", Blue);
        store.Create("Ocean", ThemePreset.Dark);
        named.SetActiveCustom("Ocean", ThemePreset.Dark);
        var snap = ThemeOverrideSnapshot.Capture("Ocean", reg, reg.Slots);
        reg.SetOverride("Theme.accent", Red);   // an edit not yet saved

        store.Rename("Ocean", "Lagoon");
        snap.Theme = "Lagoon";
        named.SetActiveCustom("Lagoon", ThemePreset.Dark);

        Assert.Equal(1, snap.CountChanges(reg, reg.Slots));
        snap.Restore(reg, reg.Slots);   // Discard after the rename → back to no override, under the new name
        Assert.False(reg.HasOverride("Theme.accent"));
    }
}
