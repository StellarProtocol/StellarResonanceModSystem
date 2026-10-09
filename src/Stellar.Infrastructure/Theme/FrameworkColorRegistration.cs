using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;

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

    // English fallback labels, used ONLY at RegisterAll time (Load(), before the hot-update assemblies — and
    // therefore the real client language — are available; see RelabelAll). Nobody sees the editor this early.
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["Theme.Accent"]         = "Accent",
        ["Theme.MenuBackground"] = "Panel background",
        ["Theme.MenuAccent"]     = "Panel accent",
        ["Theme.MenuBorder"]     = "Panel border",
        ["Theme.Warning"]        = "Warning",
        ["Theme.HudAccent"]      = "HUD accent",
    };

    // Localization keys for the same slots, resolved by RelabelAll once the real client language is known
    // (OnHotUpdateReady) and again on every later explicit language switch (LanguageChanged).
    private static readonly IReadOnlyDictionary<string, string> LabelKeys = new Dictionary<string, string>
    {
        ["Theme.Accent"]         = "theme.color.accent",
        ["Theme.MenuBackground"] = "theme.color.menuBackground",
        ["Theme.MenuAccent"]     = "theme.color.menuAccent",
        ["Theme.MenuBorder"]     = "theme.color.menuBorder",
        ["Theme.Warning"]        = "theme.color.warning",
        ["Theme.HudAccent"]      = "theme.color.hudAccent",
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

    /// <summary>Re-applies the editable slots' display labels in the active language. <see cref="RegisterAll"/>
    /// runs during <c>Load()</c>, before the hot-update assemblies (and so the real "follow" client language)
    /// are resolvable, so its labels are always the English fallback; the Host calls this once right after
    /// hot-update-ready (when the language IS resolvable) and again on every later explicit language switch.</summary>
    public static void RelabelAll(ColorRegistryService registry, ILocalization loc)
    {
        foreach (var (key, labelKey) in LabelKeys)
            registry.Relabel(key, loc.T(labelKey));
    }
}
