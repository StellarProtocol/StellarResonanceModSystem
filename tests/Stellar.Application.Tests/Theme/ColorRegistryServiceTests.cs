// tests/Stellar.Application.Tests/Theme/ColorRegistryServiceTests.cs
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Theme;

public sealed class ColorRegistryServiceTests
{
    private static IReadOnlyDictionary<ThemePreset, ColorRgba> Defaults(
        ColorRgba def, ColorRgba dark, ColorRgba light, ColorRgba crim) =>
        new Dictionary<ThemePreset, ColorRgba>
        {
            [ThemePreset.Default] = def, [ThemePreset.Dark] = dark,
            [ThemePreset.Light]  = light, [ThemePreset.Crimson] = crim,
        };

    private static (ColorRegistryService svc, FakeNamedTheme theme) New()
    {
        var theme = new FakeNamedTheme(ThemePreset.Dark);
        var svc = new ColorRegistryService(theme, new NullOverrideStore());
        return (svc, theme);
    }

    [Fact]
    public void Register_ReturnsSlot_ResolvingActivePresetDefault()
    {
        var (svc, _) = New();
        var slot = svc.Register("PlayerHUD.Stamina", "Stamina bar",
            Defaults(new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0)));
        Assert.Equal(new ColorRgba(0,1,0), slot.Value); // active = Dark
    }

    [Fact]
    public void Slot_TracksActiveThemeChange()
    {
        var (svc, theme) = New();
        var slot = svc.Register("k", "k",
            Defaults(new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0)));
        theme.SetActive(ThemePreset.Light);
        Assert.Equal(new ColorRgba(0,0,1), slot.Value);
    }

    [Fact]
    public void Resolve_UnknownKey_ReturnsMagentaSentinel()
    {
        var (svc, _) = New();
        Assert.Equal(ColorRegistryService.MissingSentinel, svc.Resolve("nope"));
    }

    [Fact]
    public void Register_DuplicateKey_Throws()
    {
        var (svc, _) = New();
        var d = Defaults(new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0));
        svc.Register("k", "k", d);
        Assert.Throws<System.ArgumentException>(() => svc.Register("k", "k2", d));
    }

    [Fact]
    public void Unregister_RemovesSlot()
    {
        var (svc, _) = New();
        var d = Defaults(new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0));
        svc.Register("k", "k", d);
        svc.Unregister("k");
        Assert.Equal(ColorRegistryService.MissingSentinel, svc.Resolve("k"));
    }

    // ---- Relabel (framework colour labels follow the active language — owner review of 15ee53a) ----

    [Fact]
    public void Relabel_ChangesOnlyTheLabel()
    {
        var (svc, _) = New();
        var d = Defaults(new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0));
        svc.Register("Theme.Accent", "Accent", d);
        var beforeColour = svc.Resolve("Theme.Accent");

        svc.Relabel("Theme.Accent", "강조색");

        var slot = Single(svc, "Theme.Accent");
        Assert.Equal("강조색", slot.Label);
        Assert.Equal("Theme", slot.Owner);              // owner untouched
        Assert.Equal(beforeColour, svc.Resolve("Theme.Accent"));   // colour (defaults) untouched
    }

    [Fact]
    public void Relabel_IsVisibleThroughSlots()
    {
        var (svc, _) = New();
        svc.Register("k", "Original", Defaults(new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0)));
        svc.Relabel("k", "Relabeled");
        Assert.Equal("Relabeled", Single(svc, "k").Label);
    }

    [Fact]
    public void Relabel_UnknownKey_IsNoOp()
    {
        var (svc, _) = New();
        svc.Register("k", "Original", Defaults(new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0)));
        var revisionBefore = svc.Revision;

        svc.Relabel("does-not-exist", "x");

        Assert.Equal("Original", Single(svc, "k").Label);
        Assert.Equal(1, svc.SlotCount);                 // no slot was added either
        Assert.Equal(revisionBefore, svc.Revision);     // and nothing was bumped
    }

    [Fact]
    public void Relabel_SameLabel_DoesNotBumpRevision()
    {
        var (svc, _) = New();
        svc.Register("k", "Same", Defaults(new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0)));
        var revisionBefore = svc.Revision;

        svc.Relabel("k", "Same");

        Assert.Equal(revisionBefore, svc.Revision);
    }

    // ---- Revision (a slot-list cache keyed on SlotCount alone misses a Relabel — owner review of 15ee53a) ----

    [Fact]
    public void Revision_ChangesOnRegisterUnregisterAndRelabel()
    {
        var (svc, _) = New();
        var d = Defaults(new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0));
        var r0 = svc.Revision;

        svc.Register("k", "k", d);
        var r1 = svc.Revision;
        Assert.NotEqual(r0, r1);

        svc.Relabel("k", "k2");
        var r2 = svc.Revision;
        Assert.NotEqual(r1, r2);

        svc.Unregister("k");
        var r3 = svc.Revision;
        Assert.NotEqual(r2, r3);
    }

    private static ColorSlotInfo Single(ColorRegistryService svc, string key)
    {
        foreach (var s in svc.Slots) if (s.Key == key) return s;
        throw new System.InvalidOperationException($"slot not found: {key}");
    }
}
