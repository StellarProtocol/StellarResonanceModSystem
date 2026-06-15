using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Phase 9b HUD-overlay palette — colours used by <c>WindowPanelStyle.HudOverlay</c>
/// chrome and the <c>PillStatus</c> chip. Renders on top of the live game world,
/// so all colours assume no enclosing panel.
/// </summary>
public interface IThemeHudColors
{
    /// <summary>Primary HUD text colour (e.g., quest tracker, party row).</summary>
    ColorRgba HudText { get; }

    /// <summary>Drop-shadow under HUD text — typically rgba(0,0,0,0.85) for legibility over bright world.</summary>
    ColorRgba HudTextShadow { get; }

    /// <summary>Accent colour used sparingly for HUD badges / brackets / status pips.</summary>
    ColorRgba HudAccent { get; }

    /// <summary>Background fill for HP / stamina / cast bars in HUD overlays.</summary>
    ColorRgba HudBarBg { get; }

    /// <summary>Background fill for chat-row / level pill chips (transparent rounded chip).</summary>
    ColorRgba HudPillBg { get; }
}
