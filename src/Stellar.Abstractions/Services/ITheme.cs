using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

// ---------------------------------------------------------------------------
// Sub-interfaces — facade-inheritance pattern (same shape as IPlayerState).
// Splitting by concern keeps each sub-interface under the 8-member analyzer
// cap; the ITheme façade itself declares zero members and inherits all the
// surface so plugin consumers don't see the split.
// ---------------------------------------------------------------------------

/// <summary>Palette + font identity facet of the active theme.</summary>
public interface IThemePalette
{
    /// <summary>The full colour palette of the active theme (accent, HP, text tokens, HUD and menu sub-palettes).</summary>
    IThemeColors Colors   { get; }
    /// <summary>Display name of the active theme font (informational; the font itself is loaded by the renderer).</summary>
    string       FontName { get; }
}


/// <summary>
/// Composition + access facet — accessors for the semantic text helpers and
/// plugin colour registry.
/// </summary>
public interface IThemeAccess
{
    /// <summary>
    /// Semantic text drawing helpers (H1/H2/H3/H4 + Body + Caption) with a
    /// global FontScale multiplier for Phase 9 user-tunable text size.
    /// </summary>
    IThemeText Text { get; }

    /// <summary>
    /// Phase 9b.5 — register plugin-owned colours (with a default per built-in
    /// preset) and read their resolved values via the returned slot handle.
    /// Register only colours a plugin owns; for a colour that should match the
    /// theme, read <see cref="IThemePalette.Colors"/> directly instead.
    /// </summary>
    IColorRegistry ColorRegistry { get; }
}

// ---------------------------------------------------------------------------
// Facade — zero declared members; all surface comes from the sub-interfaces.
// Plugins consume `ITheme` everywhere; the split is internal vocabulary so
// the analyzer's 8-member cap can be respected without spreading the surface
// across three plugin-visible types.
// ---------------------------------------------------------------------------

/// <summary>
/// Plugin-facing theme façade. The single entry point plugins receive via
/// <c>IPluginServices.Theme</c>; bundles the palette, draw helpers, semantic
/// text, and layout primitives under one type. Facade-inheritance keeps the
/// surface cohesive while letting each facet sit under the analyzer's
/// member-count cap.
/// </summary>
public interface ITheme : IThemePalette, IThemeAccess
{
}
