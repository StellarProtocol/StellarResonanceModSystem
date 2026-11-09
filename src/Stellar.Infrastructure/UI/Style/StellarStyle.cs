using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI.Style;

/// <summary>
/// The framework's own house-style kit (Stellar Design Language, KB <c>Stellar-Design-Language.md</c>) for the
/// framework windows — the Settings hub and the launcher (settings/launcher redesign, 2.30.0). A port of the plugin
/// reference kit (<c>.claude/skills/stellar-design/reference/Plugin.Style*.cs.txt</c> = Maestro's <c>Plugin.Style.cs</c>)
/// that reads the framework's LIVE theme directly instead of <c>IPluginServices.Theme</c>.
/// <para>Every colour is computed on each call and every component hands its colours to elements as <c>Func</c>s, so
/// a theme switch (or a custom-theme colour edit) repaints on the next refresh with no rebuild. Every SELF-DRAWN fill
/// (panels, tiles, pills, chips, icon tiles, hover washes) is composed opaque first and then multiplied by the window
/// opacity (SDL §5 "the whole window fades together"); text and pictures stay solid.</para>
/// <para>Internal only: no public API comes from this kit. Split across partials (500-LoC cap):
/// <c>StellarStyle.cs</c> tokens + colour math · <c>.Controls.cs</c> pills, chips, icon buttons ·
/// <c>.Blocks.cs</c> header, panels, icon tiles, list rows, confirm panel · <see cref="StellarHelpPopover"/> the "?"
/// + help popover · <see cref="StellarFeedbackLine"/> the footer feedback line.</para>
/// </summary>
/// <remarks>
/// Toolkit limits behind the numbers (WindowBuilder-Patterns.md / SDL §6): plain TextElement is a fixed 14 px on Menu
/// windows (only the styled Bold/Emphasis path honours FontSize); Row/Column Gap 0 = the DEFAULT gap (use 0.01);
/// Bold can't toggle live (on/off = two complete variants in a Conditional wrapping the WHOLE control); a Bubble
/// hugs its content height (pin with zero-width struts); round radius = side/2 − 1 (<see cref="RoundRadius"/>).
/// </remarks>
internal sealed partial class StellarStyle
{
    private readonly IThemeColors _colors;
    private readonly Func<float>? _opacity;

    /// <summary>Kit over the live theme colours; fills fade with <paramref name="chrome"/>'s window opacity
    /// (the Themes › Window Opacity slider). Null chrome = always opaque.</summary>
    public StellarStyle(IThemeColors colors, IChromeStyle? chrome)
        : this(colors, chrome == null ? null : () => chrome.WindowOpacity) { }

    /// <summary>Kit with an explicit fill-opacity source (e.g. a window with its own frame-opacity override).</summary>
    public StellarStyle(IThemeColors colors, Func<float>? opacity)
    {
        _colors = colors;
        _opacity = opacity;
    }

    // ── mixes (Stellar-Design-Language §3; same numbers as the plugin reference kit) ─────────────────────────────
    private static readonly ColorRgba PanelShade = new(0f, 0f, 0f, 0.20f);   // panel = window background darkened
    private const float TileTextMix      = 0.08f;   // tile = panel + 8 % text (lighter on dark, darker on Light)…
    private const float TileAccentMix    = 0.06f;   // … + 6 % accent (user-approved 2026-11-06)
    private const float CardTextMix      = 0.06f;   // card = WINDOW background + 6 % text …
    private const float CardAccentMix    = 0.08f;   // … + 8 % accent (sits on the window, not in a panel — R6)
    private const float NeutralMix       = 0.22f;   // neutral pill = panel + 22 % muted …
    private const float NeutralAccentMix = 0.08f;   // … + 8 % accent
    private const float HoverMix         = 0.10f;   // hover = text @10 % over the fill (follows the control's shape — R2)
    private const float WashAlpha        = 0.22f;   // accent wash / coloured chip / caution / remove-hover wash
    private const float ChipOffAlpha     = 0.15f;   // off chip = muted @15 %
    private const float DangerAlpha      = 0.85f;   // the solid Danger pill
    private const float ProblemLift      = 0.35f;   // remove-hover glyph = Danger 35 % toward white (#FF4D4F → #FF8A8B)

    /// <summary>WCAG minimum for labels on TINTED fills (Neutral, Caution, text on tiles).</summary>
    public const float TintedMinContrast = 4.5f;
    /// <summary>WCAG minimum for white bold labels on SOLID fills (Primary, Danger) — R7, user-approved option B.</summary>
    public const float SolidMinContrast = 3.0f;

    public static readonly ColorRgba White       = new(1f, 1f, 1f, 1f);
    public static readonly ColorRgba Transparent = new(0f, 0f, 0f, 0f);

    // ── raw theme colours (opaque) ───────────────────────────────────────────────────────────────────────────────
    public ColorRgba Text()       => _colors.MenuText with { A = 1f };
    public ColorRgba Muted()      => _colors.MenuMuted with { A = 1f };
    public ColorRgba Accent()     => _colors.MenuAccent with { A = 1f };
    /// <summary>The window background, opaque — the surface every token is composed over.</summary>
    public ColorRgba Background() => _colors.MenuBackground with { A = 1f };
    public ColorRgba Success()    => _colors.Success with { A = 1f };
    public ColorRgba Danger()     => _colors.Danger with { A = 1f };
    public ColorRgba Caution()    => _colors.Caution with { A = 1f };

    // ── surface tokens (opaque; wrap in Fill() when used as a Bubble fill) ───────────────────────────────────────
    public ColorRgba Panel()   => Over(PanelShade, Background());
    public ColorRgba Tile()    => Over(Accent() with { A = TileAccentMix }, Over(Text() with { A = TileTextMix }, Panel()));
    public ColorRgba Card()    => Over(Accent() with { A = CardAccentMix }, Over(Text() with { A = CardTextMix }, Background()));
    public ColorRgba Neutral() => Over(Accent() with { A = NeutralAccentMix }, Over(Muted() with { A = NeutralMix }, Panel()));
    public ColorRgba NeutralHover() => Hover(Neutral());
    public ColorRgba NeutralLabel() => EnsureContrast(Text(), Neutral(), TintedMinContrast);
    public ColorRgba Primary()      => Accent();
    public ColorRgba PrimaryLabel() => SolidLabel(Primary());
    public ColorRgba CautionFill()  => Over(Caution() with { A = WashAlpha }, Panel());
    public ColorRgba CautionLabel() => EnsureContrast(Caution(), CautionFill(), TintedMinContrast);
    public ColorRgba DangerFill()   => Over(Danger() with { A = DangerAlpha }, Panel());
    public ColorRgba DangerLabel()  => SolidLabel(DangerFill());
    /// <summary>The selected / open highlight: accent @22 % over <paramref name="surface"/> (default the panel).</summary>
    public ColorRgba AccentWash(ColorRgba? surface = null) => Over(Accent() with { A = WashAlpha }, surface ?? Panel());
    public ColorRgba ChipOff() => Over(Muted() with { A = ChipOffAlpha }, Panel());
    /// <summary>An "on" chip's fill: its colour @22 % over the panel.</summary>
    public ColorRgba ChipOn(ColorRgba color) => Over(color with { A = WashAlpha }, Panel());
    public ColorRgba RemoveHoverWash(ColorRgba surface) => Over(Danger() with { A = WashAlpha }, surface);
    public ColorRgba RemoveHoverGlyph(ColorRgba surface)
        => EnsureContrast(Mix(Danger(), White, ProblemLift), RemoveHoverWash(surface), SolidMinContrast);
    /// <summary>Text in a semantic colour, kept readable on <paramref name="surface"/> (4.5:1; darkens on Light).</summary>
    public ColorRgba Readable(ColorRgba color, ColorRgba surface) => EnsureContrast(color, surface, TintedMinContrast);
    /// <summary>Hover over any fill: text @10 % composited on it (lighter on dark themes, darker on Light).</summary>
    public ColorRgba Hover(ColorRgba fill) => Over(Text() with { A = HoverMix }, fill);
    // House rule (user, 2026-11-09): pressable and disabled must never look alike. A pressable control is a RAISED
    // fill (lighter than its surface); a disabled one is RECESSED — a sunken fill DARKER than the surface it sits on
    // (black @28 % over it), same size/radius, with a faint label. User-approved option A "Recessed" (2026-11-09),
    // replacing the transparent + 1 px muted ring look (8df7f29): the ring was the only hard stroke in a fill-based
    // design. The old "fill halfway back to the panel" still read as a (dull) button.
    private static readonly ColorRgba DisabledShade = new(0f, 0f, 0f, 0.28f);
    private const float DisabledLabelAlpha = 0.32f;      // text @32 % — faint, but still legible as a word …
    private const float DisabledLabelMinContrast = 2.0f; // … raised until it reaches 2:1 on the sunken fill (Light themes)

    /// <summary>A disabled control's sunken fill, opaque: black @28 % over <paramref name="surface"/> (the panel or tile
    /// the control sits on; default the panel). Wrap in <see cref="Fill"/> like every other fill.</summary>
    public ColorRgba DisabledFill(ColorRgba? surface = null) => Over(DisabledShade, surface ?? Panel());

    /// <summary>A disabled control's label: the theme Text at ~32 % alpha, raised in steps until it reaches 2:1 against
    /// the opaque sunken fill on <paramref name="surface"/>.</summary>
    public ColorRgba DisabledLabel(ColorRgba? surface = null)
    {
        var bg = DisabledFill(surface);
        var lb = Luminance(bg);
        var a = DisabledLabelAlpha;
        while (a < 1f && ContrastRatio(Luminance(Over(Text() with { A = a }, bg)), lb) < DisabledLabelMinContrast)
            a = MathF.Min(1f, a + 0.04f);
        return Text() with { A = a };
    }

    /// <summary>The label on a SOLID fill (Primary, Danger — R7): white while white reaches 3:1 against the fill as the
    /// window actually draws it; otherwise a near-black label at 4.5:1. The old rule nudged white toward black only
    /// until it JUST reached 3:1, which on the teal Default accent (#5FB8C4, white = 2.3:1) stopped at a mid grey
    /// #535353 (3.4:1, measured in a screenshot) — thin bold text at that contrast reads washed out.</summary>
    public ColorRgba SolidLabel(ColorRgba fill)
    {
        var seen = Over(fill with { A = Opacity }, Panel());   // the faded fill over what sits behind it (≈ the panel)
        if (ContrastRatio(1f, Luminance(seen)) >= SolidMinContrast) return White;
        return EnsureContrast(DarkLabel, seen, TintedMinContrast);
    }

    private static readonly ColorRgba DarkLabel = new(0.12f, 0.12f, 0.12f, 1f);   // #1F1F1F

    // ── window opacity (SDL §5) ──────────────────────────────────────────────────────────────────────────────────
    /// <summary>The live fill factor (Themes › Window Opacity, 0.3–1), 1 without a source.</summary>
    public float Opacity => _opacity == null ? 1f : Math.Clamp(_opacity(), 0f, 1f);

    /// <summary>A self-drawn fill as the window draws it: <paramref name="opaque"/> (composed opaque first) with its
    /// alpha × <see cref="Opacity"/>. Every Bubble FillColorFn in the kit goes through this.</summary>
    public ColorRgba Fill(ColorRgba opaque) => opaque with { A = opaque.A * Opacity };

    // ── shape ────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Corner radius of a ROUND (capsule / circle) bubble of height <paramref name="side"/>: side/2 − 1, never
    /// side/2 (R1). The bubble's 9-slice sprite border is radius + 1 and a sliced Image reports the summed borders as its
    /// MIN size, so side/2 drew every round control 2 px taller (28 px pills at 30, the 24 px "?" at 24×26).</summary>
    public static float RoundRadius(float side) => side / 2f - 1f;

    /// <summary>Tiles, cards and panels.</summary>
    public const float TileRadius = 6f;

    /// <summary>A mutable hover flag a Bare selectable's OnHover writes and a Bubble's FillColorFn reads.</summary>
    public sealed class HoverFlag { public bool On; }

    // ── colour math (ChatTools Plugin.Tabs.cs → plugin Plugin.Style.cs; pure struct math, allocation-free) ───────────
    /// <summary>Nudges <paramref name="fg"/> toward white (dark bg) or black (light bg) in 15 % steps until it reaches
    /// <paramref name="minRatio"/> against the opaque <paramref name="bg"/>.</summary>
    public static ColorRgba EnsureContrast(ColorRgba fg, ColorRgba bg, float minRatio)
    {
        var lb = Luminance(bg);
        var target = lb < 0.179f ? White : new ColorRgba(0f, 0f, 0f, 1f);   // the equal-contrast point
        for (var i = 0; i < 12 && ContrastRatio(Luminance(fg), lb) < minRatio; i++) fg = Mix(fg, target, 0.15f);
        return fg with { A = 1f };
    }

    public static float ContrastRatio(float la, float lb) => (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);

    public static float Luminance(ColorRgba c) => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);

    private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    /// <summary>a → b by t (0 = a, 1 = b), opaque.</summary>
    public static ColorRgba Mix(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    /// <summary>Alpha-composite <paramref name="fg"/> (its A = coverage) over an opaque <paramref name="bg"/>.</summary>
    public static ColorRgba Over(ColorRgba fg, ColorRgba bg) => Mix(bg, fg with { A = 1f }, fg.A);
}
