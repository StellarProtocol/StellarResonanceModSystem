namespace Stellar.Abstractions.Services;

/// <summary>
/// Semantic text drawing API for plugins, modelled after HTML's heading +
/// paragraph levels. Each method renders text at the corresponding base size
/// and weight, scaled by <see cref="FontScale"/> so a single Phase 9 user
/// setting can globally enlarge or shrink every plugin's text in one place.
///
/// Plugin usage:
///   _services.Theme.Text.DrawH2("Combat Meter");
///   _services.Theme.Text.DrawBody($"DPS: {dps:F1}");
///   _services.Theme.Text.DrawCaption("(last update 2s ago)");
///
/// Base sizes match the mockup hierarchy (theme-palette-v3 / panel-styles):
/// H1 = 20px (top-level section), H2 = 17px (subheading), H3 = 15px (group
/// label), H4 = 14px (list item header), Body = 13px (paragraph default),
/// Caption = 11px (muted footnote). All headings render in bold; Body and
/// Caption render in normal weight. Captions use the muted text colour.
/// </summary>
public interface IThemeText
{
    /// <summary>
    /// Global multiplier applied to every Draw method's base font size.
    /// 1.0 = mockup default. Phase 8 always returns 1.0; Phase 9's Settings
    /// UI exposes this so users can scale every plugin's text together.
    /// </summary>
    float FontScale { get; }

    /// <summary>20px bold — top-level section heading.</summary>
    void DrawH1(string text);

    /// <summary>17px bold — window subheading or major group label.</summary>
    void DrawH2(string text);

    /// <summary>15px bold — sub-section heading.</summary>
    void DrawH3(string text);

    /// <summary>14px bold — list item header.</summary>
    void DrawH4(string text);

    /// <summary>13px regular — body text, paragraphs, inline content.</summary>
    void DrawBody(string text);

    /// <summary>11px regular, muted colour — footnote, hint, caption.</summary>
    void DrawCaption(string text);
}
