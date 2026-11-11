using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI.Style;

/// <summary>The four pill severities (SDL R8): Danger = interrupts everyone / destructive confirm, Caution = stops only
/// the player's own part, Primary = the main action, Neutral = everything else.</summary>
internal enum PillKind { Primary, Neutral, Caution, Danger }

// Clickable controls: 28 px pills, 24 px chips, 28 px icon buttons (incl. the ✕ with the red remove-hover). Every
// clickable is a Bubble around a Bare selectable whose OnHover flips a flag the Bubble's fill reads, so the hover keeps
// the control's round shape (R2) — the default Selectable wash is a square. There is no pressed tint: the toolkit has
// no pointer-down callback for a Bare selectable.
internal sealed partial class StellarStyle
{
    // ── pills ────────────────────────────────────────────────────────────────────────────────────────────────────
    public const float PillHeight    = 28f;
    public const float PillPadX      = 12f;
    /// <summary>Label box for two pills side by side (weighted cells only split equally when both halves prefer the
    /// same width).</summary>
    public const float PillPairWidth = 96f;
    private const float PillLabelSize = 13f;   // bold 13: its line stays under the 28 px strut

    // Bold (TMP) labels sit ~1.5–2 px high in a centred line (measured in-game 2026-11-06, R9): the label goes in a
    // Column under a 2×drop spacer, which moves the cap band DOWN by BoldLabelDrop. Canvas units (scales with the UI).
    private const float BoldLabelDrop = 1f;

    /// <summary>Optically centres a BOLD label (R9; see <see cref="BoldLabelDrop"/>).</summary>
    public static HudElement DropBold(HudElement label) => new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, 2f * BoldLabelDrop),
        label,
    }, Gap: 0.01f);

    /// <summary>The ONE labelled button (SDL §4 PillButton): 28 px, round ends, bold-13 label. <paramref name="width"/>
    /// fixes the LABEL box (longest label in any locale and state — R10); 0 hugs. A disabled pill keeps its geometry,
    /// swallows the click and RECESSES: a sunken fill darker than the panel + a faint label (the house rule, StellarStyle.cs).</summary>
    public HudElement PillButton(Func<string> label, PillKind kind, Action onClick, float width = 0f,
        Func<bool>? enabled = null)
    {
        bool En() => enabled == null || enabled();
        var text = new TextElement(label, Color: () => En() ? PillLabel(kind) : DisabledLabel(),
            Width: width, Align: TextAlign.Center, NoWrap: true, FontSize: (int)PillLabelSize) { Bold = true };
        var pill = RoundPill(DropBold(text), () => PillFill(kind), onClick, enabled);
        // A fixed label box gets an explicit MaxWidth too: a MaxWidth-0 bubble placed directly in a Conditional inside a
        // Row caps itself at the 100 px default size (the Conditional takes its width FROM the bubble), so a wider pill
        // was cut (WindowBuilder-Patterns, Bubble shrink-only ratchet). Equal to the natural width, so no visual change.
        return width > 0f && pill is BubbleElement b ? b with { MaxWidth = width + 2f * PillPadX } : pill;
    }

    public HudElement PrimaryButton(Func<string> label, Action onClick, float width = 0f, Func<bool>? enabled = null)
        => PillButton(label, PillKind.Primary, onClick, width, enabled);
    public HudElement NeutralButton(Func<string> label, Action onClick, float width = 0f, Func<bool>? enabled = null)
        => PillButton(label, PillKind.Neutral, onClick, width, enabled);
    public HudElement CautionButton(Func<string> label, Action onClick, float width = 0f, Func<bool>? enabled = null)
        => PillButton(label, PillKind.Caution, onClick, width, enabled);
    public HudElement DangerButton(Func<string> label, Action onClick, float width = 0f, Func<bool>? enabled = null)
        => PillButton(label, PillKind.Danger, onClick, width, enabled);

    /// <summary>Two controls sharing a row equally (Gap 8). Give both pills the SAME label width
    /// (<see cref="PillPairWidth"/>).</summary>
    public static HudElement PillPair(HudElement left, HudElement right) => new RowElement(new HudElement[]
    {
        new CellElement(left,  Weight: 1f),
        new CellElement(right, Weight: 1f),
    }, Gap: 8f);

    public ColorRgba PillFill(PillKind kind) => kind switch
    {
        PillKind.Primary => Primary(),
        PillKind.Caution => CautionFill(),
        PillKind.Danger  => DangerFill(),
        _                => Neutral(),
    };

    public ColorRgba PillLabel(PillKind kind) => kind switch
    {
        PillKind.Primary => PrimaryLabel(),
        PillKind.Caution => CautionLabel(),
        PillKind.Danger  => DangerLabel(),
        _                => NeutralLabel(),
    };

    // The shared pill shell: Bubble → Bare Selectable → Row[strut, label, strut]. The struts carry the 28 px height and
    // the side padding, so the bubble is exactly 28 tall (with RoundRadius) whatever the label's line height.
    private HudElement RoundPill(HudElement label, Func<ColorRgba> fill, Action onClick, Func<bool>? enabled)
    {
        var hover = new HoverFlag();
        bool En() => enabled == null || enabled();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(PillPadX, PillHeight),
                label,
                new SpacerElement(PillPadX, PillHeight),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (En()) onClick(); })
            { Bare = true, OnHover = over => hover.On = over })
        {
            // Disabled = a sunken fill on the panel (house rule, StellarStyle.cs): only the colour changes, so no layout jump.
            FillColorFn = () => Fill(!En() ? DisabledFill() : hover.On ? Hover(fill()) : fill()),
            CornerRadius = RoundRadius(PillHeight), PaddingX = 0f, PaddingY = 0f,
        };
    }

    // ── chips ────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Default chip width (fits "On"/"Off" and their translations). A chip whose label varies takes the width
    /// of its LONGEST label in any locale and state, so it never resizes (R10).</summary>
    public const float ChipWidth = 64f;
    public const float ChipHeight = 24f;
    private const float ChipStrut = ChipHeight - 2f;   // + PaddingY 1 top/bottom = 24

    /// <summary>On/off chip (24 px, round). On = bold label in <paramref name="onColor"/> (default the theme Success) on a
    /// 22 % wash of it; off = regular muted label on a faint muted pill. Clickable when <paramref name="onClick"/> is set
    /// (the measured MinimalNameplate recipe: ONE fixed-width cell → a Conditional swapping two COMPLETE chips that share
    /// one hover flag, each Bubble with an explicit MaxWidth — SDL "Clickable on/off chip"). Both states 64×24.</summary>
    public HudElement Chip(Func<string> label, Func<bool> on, Action? onClick = null, float width = ChipWidth,
        Func<ColorRgba>? onColor = null)
    {
        var hover = new HoverFlag();
        ColorRgba OnColor() => onColor?.Invoke() ?? Success();
        var onChip = ChipShell(DropBold(new TextElement(label, Color: () => Readable(OnColor(), ChipOn(OnColor())),
                Align: TextAlign.Center, NoWrap: true) { Bold = true }),
            () => ChipOn(OnColor()), width, onClick, hover);
        var offChip = ChipShell(new TextElement(label, Color: () => Muted(), Align: TextAlign.Center, NoWrap: true),
            ChipOff, width, onClick, hover);
        return new CellElement(new ConditionalElement(on, onChip, offChip), Width: width);
    }

    /// <summary>Read-only status chip: coloured (bold, 22 % wash) while <paramref name="color"/> returns a colour,
    /// muted while it returns null.</summary>
    public HudElement StatusChip(Func<string> label, Func<ColorRgba?> color, float width = ChipWidth)
        => Chip(label, () => color() != null, onClick: null, width, onColor: () => color() ?? Muted());

    /// <summary>Disabled (non-interactive) chip at the same 64×24 footprint: muted label on the recessed disabled fill —
    /// the house rule that disabled never looks pressable (Rule 22). Swap it in for <see cref="Chip"/> with a
    /// ConditionalElement wrapping the whole chip, so the size never changes.</summary>
    public HudElement DisabledChip(Func<string> label, float width = ChipWidth)
        => new CellElement(ChipShell(new TextElement(label, Color: () => Muted(), Align: TextAlign.Center, NoWrap: true),
            () => DisabledFill(), width, onClick: null, new HoverFlag()), Width: width);

    private HudElement ChipShell(HudElement text, Func<ColorRgba> fill, float width, Action? onClick, HoverFlag hover)
    {
        HudElement content = new RowElement(new HudElement[]
        {
            new SpacerElement(0f, ChipStrut),   // width 0 + height > 0 = the height strut that keeps off = on
            text,
        }, Gap: 0.01f, Justify: RowJustify.Center);
        if (onClick != null)
            content = new SelectableElement(content, OnClick: onClick) { Bare = true, OnHover = over => hover.On = over };
        return new BubbleElement(content)
        {
            FillColorFn = () => Fill(onClick != null && hover.On ? Hover(fill()) : fill()),
            CornerRadius = RoundRadius(ChipHeight), PaddingX = 4f, PaddingY = 1f, MaxWidth = width,
        };
    }

    // ── icon buttons ─────────────────────────────────────────────────────────────────────────────────────────────
    public const float IconButtonSize = 28f;
    private const int IconButtonGlyph = 16;

    /// <summary>28×28 round icon-only button: transparent at rest, text @10 % circle on hover, a 16 px PNG glyph.
    /// <paramref name="active"/> (optional) tints it with the accent wash while true (e.g. a toggled mode).
    /// <paramref name="tint"/> / <paramref name="hoverTint"/> (optional) re-colour a flat WHITE glyph at rest / on hover
    /// via <see cref="ImageElement.Tint"/> (hover falls back to the rest tint). Both null = no tint = the PNG's own
    /// colours, so existing callers keep their look; the launcher passes Muted → Text (prototype icon buttons).
    /// The image is Live (re-pulls <paramref name="icon"/> each refresh, swaps on a new array) — free for a stable
    /// array, and what lets the string overload's size pick follow the canvas scale.</summary>
    public HudElement IconButton(Func<byte[]?> icon, Action onClick, Func<bool>? active = null,
        Func<ColorRgba>? tint = null, Func<ColorRgba>? hoverTint = null)
    {
        var hover = new HoverFlag();
        const float pad = (IconButtonSize - IconButtonGlyph) / 2f;
        Func<ColorRgba?>? glyphTint = tint == null && hoverTint == null ? null
            : () => hover.On && hoverTint != null ? hoverTint() : tint?.Invoke();
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(pad, IconButtonSize),
                new ImageElement(icon, IconButtonGlyph, IconButtonGlyph) { Tint = glyphTint, Live = true },
                new SpacerElement(pad, IconButtonSize),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => hover.On ? Fill(Hover(active?.Invoke() == true ? AccentWash() : Panel()))
                              : active?.Invoke() == true ? Fill(AccentWash()) : Transparent,
            CornerRadius = RoundRadius(IconButtonSize), PaddingX = 0f, PaddingY = 0f, MaxWidth = IconButtonSize,
        }, Width: IconButtonSize);
    }

    /// <summary><see cref="IconButton(Func{byte[]}, Action, Func{bool}, Func{ColorRgba}, Func{ColorRgba})"/> for a
    /// framework v2 glyph (<see cref="LauncherIcons"/> key): the bitmap pre-drawn for the 16 px glyph at the live canvas
    /// scale, re-picked when the scale changes.</summary>
    public HudElement IconButton(string launcherIcon, Action onClick, Func<bool>? active = null,
        Func<ColorRgba>? tint = null, Func<ColorRgba>? hoverTint = null)
        => IconButton(() => LauncherIcons.Glyph(launcherIcon, IconButtonGlyph), onClick, active, tint, hoverTint);

    /// <summary>28×28 icon-only ✕ (remove / close): muted glyph at rest; on hover a red glyph on the soft red round wash
    /// (house "Remove hover"). <paramref name="surface"/> = what it sits on (default the tile), for the glyph contrast.
    /// Pass <paramref name="hover"/> to own the flag — a pooled list row must reset it when its slot shows another item
    /// (a ✕ click slides the next row under the pointer; it must not inherit the red state). The ✕ is PLAIN text: bold
    /// ✓/✕ glyphs are untrusted (SDL §6).</summary>
    public HudElement CloseIconButton(Action onClick, Func<ColorRgba>? surface = null, HoverFlag? hover = null)
    {
        var h = hover ?? new HoverFlag();
        ColorRgba Surface() => surface?.Invoke() ?? Tile();
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, IconButtonSize),
                new TextElement(() => "✕", Color: () => h.On ? RemoveHoverGlyph(Surface()) : Muted(),
                    Width: IconButtonSize, Align: TextAlign.Center, NoWrap: true),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => h.On = over })
        {
            FillColorFn = () => h.On ? Fill(RemoveHoverWash(Surface())) : Transparent,
            CornerRadius = RoundRadius(IconButtonSize), PaddingX = 0f, PaddingY = 0f, MaxWidth = IconButtonSize,
        }, Width: IconButtonSize);
    }
}
