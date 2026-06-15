using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Theme;

/// <summary>
/// Per-preset palette tables — 8 colours each, indexed in the order of
/// <see cref="Stellar.Abstractions.Services.IThemeColors"/>: Accent, Gold,
/// HpFill, MpFill, Stamina, TextPrimary, TextMuted, Warning.
/// </summary>
internal static class ThemePresets
{
    public const int Accent      = 0;
    public const int Gold        = 1;
    public const int HpFill      = 2;
    public const int MpFill      = 3;
    public const int Stamina     = 4;
    public const int TextPrimary = 5;
    public const int TextMuted   = 6;
    public const int Warning     = 7;

    // Phase 9b — HUD + menu indices appended at the tail of each preset table.
    // Task 3 extends the Tables[].arrays themselves; until then, PresetColorsView
    // uses the fallback overload to handle short arrays gracefully.
    public const int HudText        = 8;
    public const int HudTextShadow  = 9;
    public const int HudAccent      = 10;
    public const int HudBarBg       = 11;
    public const int HudPillBg      = 12;
    public const int MenuBackground = 13;
    public const int MenuText       = 14;
    public const int MenuMuted      = 15;
    public const int MenuAccent     = 16;
    public const int MenuBorder     = 17;

    public static readonly IReadOnlyDictionary<ThemePreset, ColorRgba[]> Tables = new Dictionary<ThemePreset, ColorRgba[]>
    {
        // Default: the Phase 8 palette (matches ThemeColors.cs hex constants).
        [ThemePreset.Default] = new[]
        {
            Rgb(0x5F, 0xE8, 0xC5),
            Rgb(0xE8, 0xC8, 0x4A),
            Rgb(0x4C, 0xC1, 0x5C),
            Rgb(0x4A, 0xD9, 0xB8),
            Rgb(0xF4, 0xA2, 0x3F),
            Rgb(0xFF, 0xFF, 0xFF),
            Rgb(0xC4, 0xD4, 0xDD),
            Rgb(0xFF, 0x7B, 0x7B),
            // Phase 9b — HUD facet (HudText/Shadow/Accent/BarBg/PillBg)
            // World-legible + theme-invariant: only HudAccent varies per preset.
            Rgb(0xE5, 0xE1, 0xD6),
            new(0f, 0f, 0f, 0.85f),
            Rgb(0xC9, 0xA0, 0x46),
            new(16f / 255f, 20f / 255f, 26f / 255f, 0.72f),
            new(44f / 255f, 48f / 255f, 56f / 255f, 0.85f),
            // Phase 9b — menu facet (MenuBackground = top stop of 135° gradient)
            // Slate Teal (2026-05-31): cool blue-grey glass + cyan accent.
            Rgb(0x23, 0x29, 0x2F),
            Rgb(0xE3, 0xE8, 0xEA),
            Rgb(0x7E, 0x8A, 0x8E),
            Rgb(0x5F, 0xB8, 0xC4),
            new(95f / 255f, 184f / 255f, 196f / 255f, 0.22f),
        },
        // Dark: cooler accent, slightly desaturated text.
        [ThemePreset.Dark] = new[]
        {
            Rgb(0x7A, 0xB8, 0xFF),
            Rgb(0xFF, 0xD5, 0x6B),
            Rgb(0x52, 0xA3, 0x5E),
            Rgb(0x6A, 0xC4, 0xD6),
            Rgb(0xF9, 0xA2, 0x4B),
            Rgb(0xED, 0xED, 0xED),
            Rgb(0x9B, 0xB0, 0xBD),
            Rgb(0xFF, 0x8E, 0x8E),
            // Phase 9b — HUD facet (theme-invariant; only HudAccent varies)
            Rgb(0xE5, 0xE1, 0xD6),
            new(0f, 0f, 0f, 0.85f),
            Rgb(0x7F, 0xC8, 0xA9),
            new(16f / 255f, 20f / 255f, 26f / 255f, 0.72f),
            new(44f / 255f, 48f / 255f, 56f / 255f, 0.85f),
            // Phase 9b — menu facet
            Rgb(0x1C, 0x1F, 0x2E),
            Rgb(0xE5, 0xE1, 0xD6),
            Rgb(0x7A, 0x7A, 0x8A),
            Rgb(0x7F, 0xC8, 0xA9),
            new(127f / 255f, 200f / 255f, 169f / 255f, 0.30f),
        },
        // Light: light-accent theme for floating overlays — transparent over
        // the world like the others, just a blue accent (no light panels).
        [ThemePreset.Light] = new[]
        {
            Rgb(0x00, 0x72, 0xB8),
            Rgb(0xB0, 0x8A, 0x1B),
            Rgb(0x46, 0xC8, 0x5E),
            Rgb(0x1F, 0x87, 0xA3),
            Rgb(0xF0, 0xA5, 0x3C),
            // Light = light-ACCENT theme for floating overlays, not light panels.
            // Body text stays light so it reads over the transparent-over-world
            // chromes. Dark text here was unreadable on the SettingsDialog dark
            // body AND forced the opaque white tool-panel box (now removed).
            Rgb(0xED, 0xED, 0xED),
            Rgb(0x9B, 0xB0, 0xBD),
            Rgb(0xC2, 0x45, 0x45),
            // Phase 9b — HUD facet: world-legible + theme-invariant. The HUD
            // floats on the game world (no theme) — light text + dark shadow +
            // dark bar track + dark pill chip in ALL presets. Only HudAccent varies.
            Rgb(0xE5, 0xE1, 0xD6),
            new(0f, 0f, 0f, 0.85f),
            Rgb(0x5A, 0x88, 0x70),
            new(16f / 255f, 20f / 255f, 26f / 255f, 0.72f),
            new(44f / 255f, 48f / 255f, 56f / 255f, 0.85f),
            // Phase 9b — menu facet (light gradient)
            Rgb(0xF5, 0xF5, 0xFF),
            Rgb(0x1A, 0x1A, 0x1F),
            Rgb(0x88, 0x88, 0x88),
            Rgb(0x5A, 0x88, 0x70),
            new(90f / 255f, 136f / 255f, 112f / 255f, 0.45f),
        },
        // Crimson: warm-accent variant.
        [ThemePreset.Crimson] = new[]
        {
            Rgb(0xFF, 0x5A, 0x5A),
            Rgb(0xFF, 0xB3, 0x47),
            Rgb(0xE0, 0x48, 0x48),
            Rgb(0xC5, 0xBB, 0xA0),
            Rgb(0xFF, 0xB8, 0x71),
            Rgb(0xFF, 0xFF, 0xFF),
            Rgb(0xD6, 0xC0, 0xC0),
            Rgb(0xFF, 0xE0, 0x66),
            // Phase 9b — HUD facet (theme-invariant; only HudAccent varies)
            Rgb(0xF0, 0xE2, 0xE0),
            new(0f, 0f, 0f, 0.85f),
            Rgb(0xC8, 0x48, 0x60),
            new(16f / 255f, 20f / 255f, 26f / 255f, 0.72f),
            new(44f / 255f, 48f / 255f, 56f / 255f, 0.85f),
            // Phase 9b — menu facet
            Rgb(0x2A, 0x1A, 0x1D),
            Rgb(0xF0, 0xE2, 0xE0),
            Rgb(0x8A, 0x6A, 0x70),
            Rgb(0xC8, 0x48, 0x60),
            new(220f / 255f, 70f / 255f, 90f / 255f, 0.12f),
        },
    };

    /// <summary>
    /// 135° linear gradient stops for the <c>GlassMenu</c> chrome — Phase 9b.
    /// Three stops (top → mid → bottom) per preset, baked into the
    /// <c>_glassMenuBgTex</c> texture by <c>MakeGlassMenuBgTexture</c> in
    /// <c>ThemeRenderer.GlassMenuTextures.cs</c> (added in Phase 9b Task 6).
    /// </summary>
    public static readonly IReadOnlyDictionary<ThemePreset, ColorRgba[]> GlassMenuGradients = new Dictionary<ThemePreset, ColorRgba[]>
    {
        [ThemePreset.Default] = new[] { Rgb(0x23, 0x29, 0x2F), Rgb(0x1E, 0x24, 0x29), Rgb(0x23, 0x2B, 0x30) },
        [ThemePreset.Dark]    = new[] { Rgb(0x1C, 0x1F, 0x2E), Rgb(0x18, 0x1A, 0x26), Rgb(0x1F, 0x1C, 0x2E) },
        [ThemePreset.Light]   = new[] { Rgb(0xF5, 0xF5, 0xFF), Rgb(0xE8, 0xEF, 0xFA), Rgb(0xED, 0xE8, 0xFA) },
        [ThemePreset.Crimson] = new[] { Rgb(0x2A, 0x1A, 0x1D), Rgb(0x25, 0x16, 0x18), Rgb(0x2A, 0x18, 0x1B) },
    };

    private static ColorRgba Rgb(byte r, byte g, byte b)
        => new(r / 255f, g / 255f, b / 255f, 1f);
}
