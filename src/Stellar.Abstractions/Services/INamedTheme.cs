using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// User-facing theme controls — preset selector + global font scale. The
/// concrete implementation persists selections to config and tints every
/// plugin's rendered chrome / text on the next OnGUI pass.
/// </summary>
public interface INamedTheme
{
    /// <summary>Currently active built-in theme preset (base preset when a custom theme is active).</summary>
    ThemePreset Active    { get; }
    /// <summary>Global text-size multiplier in the range [0.8, 1.4]. Applied to all plugin window text.</summary>
    float       FontScale { get; }

    /// <summary>Fired when Active or FontScale changes.</summary>
    event Action ActiveChanged;

    /// <summary>Switches to the built-in <paramref name="preset"/> and persists the selection.</summary>
    void SetActive(ThemePreset preset);

    /// <summary>Clamped to [0.8, 1.4] by the implementation.</summary>
    void SetFontScale(float scale);

    /// <summary>Name of the active custom theme, or null when a built-in preset
    /// is active. When non-null, <see cref="Active"/> reports the custom theme's
    /// base preset (so existing preset-based readers keep working).</summary>
    string? ActiveCustomName { get; }

    /// <summary>Activate a custom theme by name, recording its base preset.</summary>
    void SetActiveCustom(string name, ThemePreset basePreset);

    /// <summary>Raise <see cref="ActiveChanged"/> without changing the active
    /// theme — used by the colour editor after a custom-theme override edit so
    /// renderers that bake textures from theme colours rebuild.</summary>
    void NotifyColorsChanged();
}
