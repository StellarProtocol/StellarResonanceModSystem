using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>Read-only colour lookup for a theme that need not be active — the Settings › Themes cards draw every
/// custom theme's swatch in ITS OWN colours (stored overrides over its base preset) without activating it. A separate
/// interface because <see cref="IThemeOverrides"/> is at the STELLAR0005 member cap.</summary>
internal interface IThemeSwatchResolver
{
    /// <summary><paramref name="slotKey"/> for custom theme <paramref name="customName"/> (null = the built-in
    /// <paramref name="basePreset"/> itself): its stored override, else the base preset's default — the same fallback
    /// the live theme uses. Allocation-free (a tuple-keyed dictionary hit), so it is safe to poll per refresh.</summary>
    ColorRgba ResolveFor(string? customName, ThemePreset basePreset, string slotKey);
}
