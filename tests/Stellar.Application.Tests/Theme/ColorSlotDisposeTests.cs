// tests/Stellar.Application.Tests/Theme/ColorSlotDisposeTests.cs
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Theme;

/// <summary>
/// Covers the <see cref="IColorRegistry.Register(string,string,ColorRgba)"/> convenience overload
/// (all-preset default) and the <see cref="IColorSlot.Dispose"/> unregister round-trip.
/// </summary>
public sealed class ColorSlotDisposeTests
{
    private static (ColorRegistryService svc, FakeNamedTheme theme) New()
    {
        var theme = new FakeNamedTheme(ThemePreset.Dark);
        var svc = new ColorRegistryService(theme, new NullOverrideStore());
        return (svc, theme);
    }

    [Fact]
    public void Register_AllPreset_ResolvesGivenColorForEveryPreset()
    {
        var (svc, theme) = New();
        var color = new ColorRgba(0.1f, 0.2f, 0.3f, 1f);
        var slot = svc.Register("Test.Color", "Test color", color);

        // Check every preset resolves to the same supplied color.
        foreach (ThemePreset preset in System.Enum.GetValues(typeof(ThemePreset)))
        {
            theme.SetActive(preset);
            Assert.Equal(color, slot.Value);
        }
    }

    [Fact]
    public void Register_AllPreset_DefaultPreset_ResolvesCorrectly()
    {
        var (svc, _) = New();
        var color = new ColorRgba(1f, 0f, 0f, 1f);
        var slot = svc.Register("Owner.Slot", "label", color);
        Assert.Equal(color, slot.Value);
    }

    [Fact]
    public void Dispose_UnregistersSlot_FallsBackToSentinel()
    {
        var (svc, _) = New();
        var color = new ColorRgba(0f, 1f, 0f, 1f);
        var slot = svc.Register("Owner.Disposable", "label", color);

        // Pre-dispose: resolves correctly.
        Assert.Equal(color, slot.Value);

        // Act: dispose removes the slot.
        slot.Dispose();

        // Post-dispose: resolves to sentinel (unregistered).
        Assert.Equal(ColorRegistryService.MissingSentinel, svc.Resolve("Owner.Disposable"));
    }

    [Fact]
    public void Dispose_DoubleDispose_DoesNotThrow()
    {
        var (svc, _) = New();
        var slot = svc.Register("Owner.DoubleDispose", "label", new ColorRgba(1, 1, 1, 1));
        slot.Dispose();
        // Second dispose must be a no-op, not throw.
        var ex = Record.Exception(() => slot.Dispose());
        Assert.Null(ex);
    }
}
