using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

// BarElement + BarStyle, split out of HudElement.cs (500-LoC file gate; no API change).

/// <summary>Graphical fill bar (0..1). Chrome framework-themed; <paramref name="Fill"/> is the plugin's
/// semantic colour (from its colour slot). Optional right-aligned numeric <paramref name="Label"/> and
/// optional fixed-width left <paramref name="Prefix"/> caption (e.g. "HP" / "Stamina") so stacked bars
/// align in a column.</summary>
/// <param name="Fraction01">Fill fraction 0..1, re-pulled each refresh.</param>
/// <param name="Fill">Semantic fill colour (from the plugin's colour slot).</param>
/// <param name="Label">Optional numeric/status text; right-aligned beside the bar, or centred on it when <see cref="LabelInside"/> is true.</param>
/// <param name="Prefix">Optional fixed-width left caption (e.g. "HP") so stacked bars align in a column.</param>
public sealed record BarElement(
    Func<float> Fraction01, ColorRgba Fill, Func<string>? Label = null, string? Prefix = null) : HudElement
{
    /// <summary>Bar thickness in px; 0 = framework default (14).</summary>
    public float Height        { get; init; }
    /// <summary>Fixed track width in px; 0 = framework default (150). Ignored when <see cref="FillWidth"/> is true.</summary>
    public float Width         { get; init; }
    /// <summary>When true, the track flexes to fill the row width (overrides <see cref="Width"/>).</summary>
    public bool  FillWidth     { get; init; }
    /// <summary>Label/prefix font size in px; 0 = framework default (12).</summary>
    public int   LabelFontSize { get; init; }
    /// <summary>When true, the label is overlaid centred ON the bar; false (default) keeps the beside-the-bar layout.</summary>
    public bool  LabelInside   { get; init; }
    /// <summary>Bar label colour; null (default) = the theme's muted text colour.</summary>
    public ColorRgba? LabelColor { get; init; }
    /// <summary><see cref="BarStyle.Modern"/> primary (left/inside) label colour, evaluated PER REFRESH so it can
    /// track which item occupies a reused/pooled row slot — the static <see cref="LabelColor"/> is captured once and
    /// cannot (e.g. the Nearby Monsters list colouring a boss name red vs. an elite yellow in the same recycled row).
    /// Returning null falls back to <see cref="LabelColor"/> if set, else today's default (white). Default null →
    /// unchanged; a Modern bar that sets neither renders the plain white overlay exactly as before. Added as an init
    /// property (NOT a ctor param) so the record stays source + binary compatible with already-compiled plugins (see
    /// WindowBuilder-Patterns.md "Binary compatibility rules"). Honoured only on the <see cref="BarStyle.Modern"/>
    /// path; the Default bar path keeps using the static <see cref="LabelColor"/>.</summary>
    public Func<ColorRgba?>? LabelColorFn { get; init; }
    /// <summary><see cref="BarStyle.Modern"/> fill colour, evaluated PER REFRESH so it can track which item occupies
    /// a reused/pooled row slot — the static <see cref="Fill"/> ctor arg is captured once and cannot (e.g. the Nearby
    /// Monsters list colouring the HP fill by monster rank in the same recycled row). <see cref="FillColorFn"/>
    /// non-null wins; returning null falls back to the static <see cref="Fill"/>. Default null → unchanged; a Modern
    /// bar that leaves it null renders with the static <see cref="Fill"/> exactly as before, with no override
    /// machinery attached. Added as an init property (NOT a positional ctor param) so the record stays source +
    /// binary compatible with already-compiled plugins (see WindowBuilder-Patterns.md "Binary compatibility rules").
    /// Honoured on the <see cref="BarStyle.Modern"/> path and the Default (window) bar path.</summary>
    public Func<ColorRgba?>? FillColorFn { get; init; }
    /// <summary>Visual style. <see cref="BarStyle.Default"/> (0) = today's rounded-pill render; <see cref="BarStyle.Modern"/>
    /// = the CombatMeter metric-bar look (flat translucent track + flat role-coloured anchor-clipped fill + dual
    /// left/right overlay text). Existing bars omit it → render unchanged.</summary>
    public BarStyle      Style          { get; init; }
    /// <summary><see cref="BarStyle.Modern"/> only: right-edge text (e.g. a running total); <see cref="BarElement.Label"/>
    /// is then the left/primary text. Ignored in <see cref="BarStyle.Default"/>.</summary>
    public Func<string>? SecondaryLabel { get; init; }
    /// <summary><see cref="BarStyle.Modern"/> only: an animated soft-white sheen band that sweeps across the fill.
    /// Default false. Ignored in <see cref="BarStyle.Default"/>.</summary>
    public bool          Sheen          { get; init; }
    /// <summary><see cref="BarStyle.Modern"/> only: optional SECOND fill fraction 0..1 drawn on the SAME track as
    /// <see cref="Fraction01"/>, re-pulled each refresh. Null (default) = no overlay → bar renders unchanged.
    /// Pair with <see cref="OverlayColor"/> and <see cref="OverlayInFront"/> to show a second value (e.g. a
    /// monster shield on an HP bar). Ignored in <see cref="BarStyle.Default"/>.</summary>
    public Func<float>?  Overlay01      { get; init; }
    /// <summary><see cref="BarStyle.Modern"/> only: colour of the <see cref="Overlay01"/> fill. Include an alpha
    /// &lt; 1 for a translucent band (typical when <see cref="OverlayInFront"/> is true). Ignored when
    /// <see cref="Overlay01"/> is null or in <see cref="BarStyle.Default"/>.</summary>
    public ColorRgba     OverlayColor   { get; init; }
    /// <summary><see cref="BarStyle.Modern"/> only: draw order of the <see cref="Overlay01"/> fill relative to the
    /// main fill. <c>true</c> = draw OVER the main fill (a translucent band layered on top — e.g. a shield tint over
    /// HP). <c>false</c> (default) = draw BEHIND the main fill so the opaque main fill covers it, leaving only the
    /// excess visible as an "extension cap" past the main fill (e.g. <c>Overlay01 = (hp+shield)/max</c> shows the
    /// shield as extra length). Ignored when <see cref="Overlay01"/> is null or in <see cref="BarStyle.Default"/>.</summary>
    public bool          OverlayInFront { get; init; }
    /// <summary><see cref="BarStyle.Default"/> window bar only (framework 2.33.0): corner radius in px of BOTH the track
    /// and the fill, so the fill is a rounded bar too (default bars have a square fill). A radius larger than half the
    /// bar <see cref="Height"/> is drawn down-scaled so the bar never grows past its height (radius 2 on a 4 px bar =
    /// a capsule). 0 (the default) = today's 3 px track + square fill, unchanged. Init property (binary-compatible).</summary>
    public float CornerRadius { get; init; }
    /// <summary><see cref="BarStyle.Default"/> window bar only (framework 2.33.0): track colour, evaluated per refresh
    /// and painted only when it changes (alpha honoured — e.g. a theme "track" token). Returning null keeps the default
    /// translucent dark track. Default null → no override machinery, unchanged. Init property (binary-compatible).</summary>
    public Func<ColorRgba?>? TrackColorFn { get; init; }
}

/// <summary>Render style for a <see cref="BarElement"/>.</summary>
public enum BarStyle
{
    /// <summary>Today's rounded 9-slice pill track with a filled-image fill and a single side/inside label (default).</summary>
    Default,
    /// <summary>The CombatMeter metric-bar look: a flat translucent track, a flat role-coloured anchor-clipped fill, and
    /// dual left (<see cref="BarElement.Label"/>) / right (<see cref="BarElement.SecondaryLabel"/>) overlay text.</summary>
    Modern,
}

