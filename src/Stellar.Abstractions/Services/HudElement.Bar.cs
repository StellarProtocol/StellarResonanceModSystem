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
    /// <summary>Window bar (framework 2.33.0; also honoured by <see cref="BarStyle.Modern"/> window bars since 2.34.0):
    /// track colour, evaluated per refresh and painted only when it changes (alpha honoured — e.g. a theme "track" token,
    /// or fully transparent over plugin frame art). Returning null keeps the default track. Default null → no override
    /// machinery, unchanged. Init property (binary-compatible).</summary>
    public Func<ColorRgba?>? TrackColorFn { get; init; }

    // ── Framework 2.34.0: BarStyle.Modern WINDOW bar opt-ins (a boss-HP-bar look). All init properties; every default
    //    leaves the bar exactly as before. Ignored by BarStyle.Default bars and by HudOverlay-surface/IHud bars. ──

    /// <summary>Modern (2.34.0): a vertical gradient fill, top (<c>At</c> 0) → bottom (<c>At</c> 1), 2+ stops in order.
    /// Baked once at build into a small texture; <see cref="BarElement.Fill"/> is then ignored and
    /// <see cref="FillColorFn"/>, when set, MULTIPLIES the gradient (return white or null for its own colours).
    /// Null or fewer than 2 stops (default) = the flat <see cref="BarElement.Fill"/>, unchanged.</summary>
    public IReadOnlyList<GradientStop>? FillGradient { get; init; }
    /// <summary>Modern (2.34.0): a subtle static top gloss over the fill — white 18 % → 0 over the top third of the bar (not
    /// animated, unlike <see cref="Sheen"/>). Default false = none.</summary>
    public bool Gloss { get; init; }
    /// <summary>Modern (2.34.0): a soft glow of this colour around the main fill (follows its width; alpha = the glow's
    /// peak). Radius <see cref="GlowRadius"/>. Null (default) = none.</summary>
    public ColorRgba? FillGlow { get; init; }
    /// <summary>Modern (2.34.0): a "damage trail" fraction 0..1, drawn BEHIND the main fill and the overlay (so only the
    /// part past the fill shows), re-pulled each refresh. The plugin owns the lag (hold, then ease down). Null (default)
    /// = none. Colour <see cref="TrailColor"/>. Independent of <see cref="Overlay01"/>, so both can show at once.</summary>
    public Func<float>? Trail01 { get; init; }
    /// <summary>Modern (2.34.0): colour of the <see cref="Trail01"/> band (alpha honoured).</summary>
    public ColorRgba TrailColor { get; init; }
    /// <summary>Modern (2.34.0): the overlay's START fraction 0..1, re-pulled each refresh. With it the overlay is the
    /// segment [<c>OverlayStart01</c>, <see cref="Overlay01"/>] (e.g. a shield band right after the HP fill: start =
    /// hp/max, end = (hp+shield)/max) instead of [0, <see cref="Overlay01"/>]. Null (default) = starts at 0, unchanged.</summary>
    public Func<float>? OverlayStart01 { get; init; }
    /// <summary>Modern (2.34.0): draw the overlay as 135° diagonal stripes alternating <see cref="OverlayColor"/> and
    /// this colour (stripe width <see cref="OverlayStripeWidth"/>). Null (default) = a flat overlay, unchanged.</summary>
    public ColorRgba? OverlayStripeColor { get; init; }
    /// <summary>Modern (2.34.0): width in px of each overlay stripe, measured across the stripe; 0 = 4.</summary>
    public float OverlayStripeWidth { get; init; }
    /// <summary>Modern (2.34.0): a soft glow of this colour around the overlay segment. Null (default) = none.</summary>
    public ColorRgba? OverlayGlow { get; init; }
    /// <summary>Modern (2.34.0): how far <see cref="FillGlow"/> / <see cref="OverlayGlow"/> reach past the edge, px; 0 = 6.</summary>
    public float GlowRadius { get; init; }
    /// <summary>Modern (2.34.0): split the track into this many equal segments with N−1 thin dividers drawn over the
    /// fills and under the labels (10 = 10 % ticks; a wide <see cref="TickWidth"/> reads as a segmented gauge).
    /// Static, follow the bar width with no per-refresh work. 0/1 (default) = none; capped at 100.</summary>
    public int Ticks { get; init; }
    /// <summary>Modern (2.34.0): divider colour of <see cref="Ticks"/> (alpha honoured).</summary>
    public ColorRgba TickColor { get; init; }
    /// <summary>Modern (2.34.0): divider width in px of <see cref="Ticks"/>; 0 = 1.</summary>
    public float TickWidth { get; init; }
    /// <summary>Modern (2.34.0): where the primary <see cref="BarElement.Label"/> sits — <see cref="TextAlign.Left"/>
    /// (default, unchanged), <see cref="TextAlign.Center"/> or <see cref="TextAlign.Right"/>.</summary>
    public TextAlign LabelAlign { get; init; }
    /// <summary>Modern (2.34.0): the <see cref="SecondaryLabel"/> colour, evaluated per refresh (e.g. a percent that
    /// turns red when low). Null Func or a null result = white, unchanged.</summary>
    public Func<ColorRgba?>? SecondaryLabelColorFn { get; init; }
    /// <summary>Modern (2.34.0): give both overlay labels the dark readability outline (reads over any fill colour).
    /// Default false = unchanged.</summary>
    public bool LabelOutline { get; init; }
    /// <summary>Modern (2.34.0): left/right inset of the overlay labels in px; 0 = 5 (unchanged).</summary>
    public float LabelInset { get; init; }
}

/// <summary>One colour stop of <see cref="BarElement.FillGradient"/>: <paramref name="At"/> 0 = top … 1 = bottom.</summary>
public readonly record struct GradientStop(float At, ColorRgba Color);

/// <summary>Render style for a <see cref="BarElement"/>.</summary>
public enum BarStyle
{
    /// <summary>Today's rounded 9-slice pill track with a filled-image fill and a single side/inside label (default).</summary>
    Default,
    /// <summary>The CombatMeter metric-bar look: a flat translucent track, a flat role-coloured anchor-clipped fill, and
    /// dual left (<see cref="BarElement.Label"/>) / right (<see cref="BarElement.SecondaryLabel"/>) overlay text.</summary>
    Modern,
}

