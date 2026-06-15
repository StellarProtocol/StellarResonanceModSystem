using System;

namespace Stellar.Abstractions.Domain;

/// <summary>Visual chrome style for a registered plugin window. Passed via <see cref="WindowSpec.Style"/>.</summary>
public enum WindowPanelStyle
{
    /// <summary>Party-panel chrome: opaque dark panel with accent border, for party/roster type content.</summary>
    Party = 0,
    /// <summary>Tracker chrome: semi-transparent panel for objective / quest tracker content.</summary>
    Tracker = 1,
    /// <summary>No chrome: transparent background, no border, no title bar — plugin draws its own content.</summary>
    Borderless = 2,
    /// <summary>Custom chrome: plugin supplies all draw logic via its own element tree.</summary>
    Custom = 3,

    /// <summary>
    /// Phase 9b HUD overlay chrome — fully transparent container. No box,
    /// no border, no title bar. Per-element treatment only: framework
    /// helpers (DrawHpBar / DrawCaption / DrawBody) consume the
    /// <see cref="Stellar.Abstractions.Services.IThemeHudColors"/> tokens
    /// (HudText, HudTextShadow, HudPillBg, HudBarBg) to draw a 1 px text
    /// drop-shadow and pill-chip + bar backgrounds inline. Used by
    /// PlayerHUD (pilot), CooldownBar, ChatTools.
    /// </summary>
    HudOverlay = 5,

    /// <summary>
    /// Phase 9b menu / tool-panel chrome — light/dark gradient backdrop
    /// (135° 3-stop linear, baked per preset to a 256×256 texture with
    /// rounded corners baked into the alpha channel). 1 px theme-tinted
    /// border, inline title text on the gradient, ✕ close glyph with
    /// MenuAccent hover, drag handle on the title bar minus the close
    /// hit area. Mirrors the game's Profile / Modules / Inventory mood.
    /// Used by Settings hub (post-9c) + DebugInfo / DataInspector /
    /// StatInspector / ModuleOptimizer in Phase 9c.
    /// </summary>
    GlassMenu = 6,

    /// <summary>
    /// Phase 9b status-pill chrome — single-line rounded chip floating on
    /// the game world. ~14 px corner radius, padding 6/12 px, reuses
    /// <see cref="Stellar.Abstractions.Services.IThemeHudColors.HudPillBg"/>
    /// (shared with HudOverlay's level pill so the two chip-style chromes
    /// stay visually coherent). Whole-pill drag handle, no close button by
    /// design. Single-line content only — overflowing text spills outside
    /// the capsule background. Used by AutoNav nav-pill (post-9c).
    /// </summary>
    PillStatus = 7,
}

/// <summary>Logical category for a plugin window, used to group windows in the Settings layout editor.</summary>
public enum WindowCategory
{
    /// <summary>Gameplay heads-up displays (meters, cooldowns, party frames).</summary>
    HUD = 0,
    /// <summary>Interactive tool panels (module optimizer, data inspector, settings).</summary>
    Tools = 1,
    /// <summary>Developer / diagnostic windows (debug info, perf overlay).</summary>
    Debug = 2,
}

/// <summary>Bit-flag set of keyboard modifier keys used in <see cref="KeyBinding"/>.</summary>
[Flags]
public enum ModifierKeys
{
    /// <summary>No modifier keys held.</summary>
    None  = 0,
    /// <summary>Shift key held.</summary>
    Shift = 1,
    /// <summary>Control key held.</summary>
    Ctrl  = 2,
    /// <summary>Alt key held.</summary>
    Alt   = 4,
}

/// <summary>Visual style for chrome buttons (GlassMenu panels). See <see cref="Stellar.Abstractions.Services.IChromeStyle"/>.</summary>
public enum MenuButtonStyle
{
    /// <summary>Transparent fill + accent border + light label. The default.</summary>
    Outline = 0,
    /// <summary>Solid accent fill + dark label. Bold/primary.</summary>
    Filled  = 1,
    /// <summary>Faint glass fill + thin accent border + light label. Low-emphasis.</summary>
    Glass   = 2,
}

/// <summary>Visual style for the vertical scrollbar (GlassMenu panels).</summary>
public enum MenuScrollbarStyle
{
    /// <summary>Thin accent thumb, no track, no arrow buttons. The default.</summary>
    ThumbOnly = 0,
    /// <summary>Faint track + muted thumb, no arrow buttons.</summary>
    ThinTrack = 1,
}
