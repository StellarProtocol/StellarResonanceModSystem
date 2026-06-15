using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Theme;

/// <summary>
/// Phase 9b.5 — registers the framework's EDITABLE chrome colours into the
/// colour registry under the "Theme" owner, with a per-preset default read from
/// <see cref="ThemePresets.Tables"/>. These are the slots the custom-theme
/// editor exposes as shared "Theme colours". Non-editable tokens are NOT
/// registered and keep resolving straight from the static tables in
/// <c>PresetColorsView</c>.
/// </summary>
internal static class FrameworkColorRegistration
{
    /// <summary>Slot key → token index (see <see cref="ThemePresets"/> index
    /// constants). <c>PresetColorsView</c> reverses this map so the same tokens
    /// resolve through the registry.</summary>
    public static readonly IReadOnlyDictionary<string, int> EditableTokens = new Dictionary<string, int>
    {
        ["Theme.Accent"]         = ThemePresets.Accent,
        ["Theme.MenuBackground"] = ThemePresets.MenuBackground,
        ["Theme.MenuAccent"]     = ThemePresets.MenuAccent,
        ["Theme.MenuBorder"]     = ThemePresets.MenuBorder,
        ["Theme.Warning"]        = ThemePresets.Warning,
        ["Theme.HudAccent"]      = ThemePresets.HudAccent,
    };

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["Theme.Accent"]         = "Accent",
        ["Theme.MenuBackground"] = "Panel background",
        ["Theme.MenuAccent"]     = "Panel accent",
        ["Theme.MenuBorder"]     = "Panel border",
        ["Theme.Warning"]        = "Warning",
        ["Theme.HudAccent"]      = "HUD accent",
    };

    private static readonly ThemePreset[] AllPresets =
        { ThemePreset.Default, ThemePreset.Dark, ThemePreset.Light, ThemePreset.Crimson };

    public static void RegisterAll(IColorRegistry registry)
    {
        foreach (var (key, index) in EditableTokens)
        {
            var defaults = new Dictionary<ThemePreset, ColorRgba>();
            foreach (var preset in AllPresets)
                if (ThemePresets.Tables.TryGetValue(preset, out var table) && index < table.Length)
                    defaults[preset] = table[index];
            registry.Register(key, Labels[key], defaults);
        }
    }
}
