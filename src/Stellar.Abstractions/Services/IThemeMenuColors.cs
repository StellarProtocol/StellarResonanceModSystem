using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Phase 9b menu / tool-panel palette — colours used by
/// <c>WindowPanelStyle.GlassMenu</c> chrome. Renders as a light gradient panel
/// with subtle border and shadow, matching the game's Profile / Modules
/// visual language.
/// </summary>
public interface IThemeMenuColors
{
    /// <summary>Top-stop of the 135° linear gradient (or solid fill when used as a single colour).</summary>
    ColorRgba MenuBackground { get; }

    /// <summary>Primary text colour on menu surface (dark on light, light on dark presets).</summary>
    ColorRgba MenuText { get; }

    /// <summary>Secondary text colour for muted labels, captions, pill-tab inactive.</summary>
    ColorRgba MenuMuted { get; }

    /// <summary>Menu accent — pill-tab active, »»» decorators, 👍 indicator badges.</summary>
    ColorRgba MenuAccent { get; }

    /// <summary>1-px panel border colour (ultra-subtle, theme-tinted).</summary>
    ColorRgba MenuBorder { get; }
}
