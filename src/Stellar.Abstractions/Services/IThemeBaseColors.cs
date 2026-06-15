using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Original Phase 8 colour vocabulary — preserved verbatim. Extracted into its
/// own facet by Phase 9b so <see cref="IThemeColors"/> can remain at the
/// STELLAR0005 8-member cap while gaining HUD + menu colour sub-facets.
/// </summary>
public interface IThemeBaseColors
{
    /// <summary>Primary accent colour used for highlights, borders, and interactive elements.</summary>
    ColorRgba Accent      { get; }
    /// <summary>Gold / currency accent colour for reward and currency UI elements.</summary>
    ColorRgba Gold        { get; }
    /// <summary>HP bar fill colour.</summary>
    ColorRgba HpFill      { get; }
    /// <summary>MP / mana bar fill colour (currently unused in BPSR; reserved for future phases).</summary>
    ColorRgba MpFill      { get; }
    /// <summary>Stamina (origin energy) bar fill colour.</summary>
    ColorRgba Stamina     { get; }
    /// <summary>Primary body text colour.</summary>
    ColorRgba TextPrimary { get; }
    /// <summary>Secondary / muted text colour for de-emphasised labels.</summary>
    ColorRgba TextMuted   { get; }
    /// <summary>Warning / alert colour for error and attention states.</summary>
    ColorRgba Warning     { get; }
}
