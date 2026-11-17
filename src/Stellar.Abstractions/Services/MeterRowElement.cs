using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

// The CombatMeter row elements, split out of WindowElement.cs (500-LoC file gate) when MeterRowElement grew its
// opt-in rounded style. Pure relocation of the two records; their shapes are unchanged.

/// <summary>Wraps a row <paramref name="Child"/> with a per-row accent backdrop drawn BEHIND it: a faint
/// <paramref name="Stripe"/>-tinted bar whose width is <paramref name="Share"/> (0..1) of the row, plus a 3-px
/// <paramref name="Stripe"/>-coloured left edge. The CombatMeter History/Skill table rows use this for the
/// role-coloured stripe + share-fraction wash the IMGUI build drew (DrawRowAccent). Both Funcs are poll-diffed.</summary>
public sealed record AccentRowElement(HudElement Child, Func<ColorRgba> Stripe, Func<float> Share) : HudElement
{
    /// <summary>Opt-in rounded wash (framework 2.33.0). &gt; 0 = drop the 3 px left stripe and draw the share wash with
    /// corners of this radius in px, so it sits flush inside a rounded tile of the same radius (e.g. a
    /// <c>BubbleElement</c> with <c>CornerRadius = 6</c>): the left end follows the tile's rounded corners, the right end
    /// is a straight cut (as if the tile clipped it), and only within <c>radius</c> px of the row's right edge does it
    /// round off with the tile. A share narrower than the corner draws a proportionally squashed corner. 0 (the
    /// default) = the original square wash + stripe, unchanged. Init property, NOT a ctor param, so the record's
    /// primary constructor stays binary-compatible.</summary>
    public float CornerRadius { get; init; }

    /// <summary>Opacity (0..1) of the share wash, multiplied into the <see cref="Stripe"/> colour (framework 2.33.0).
    /// 0 (the default) = the original 0.12. Init property (binary-compatible).</summary>
    public float WashAlpha { get; init; }
}

/// <summary>One bespoke CombatMeter row — the borderless, role-coloured, animated meter row (HP spine + class
/// crest + name·spec·share line + role-coloured metric bar with a per-second/total overlay + self highlight +
/// offline scrim). The framework reproduces the custom visual that the IMGUI <c>MeterRowView</c> drew, so the
/// meter keeps its distinct look (it does NOT ride the generic Row/Bar primitives). <paramref name="Data"/> is
/// re-pulled on the window's capped refresh (poll-diffed) — the plugin snapshots its per-combatant state so the
/// Func never allocates. Fixed 48-px row height; width fills the row. Compose inside a <see cref="ListElement"/>.</summary>
/// <param name="Data">Per-combatant row snapshot, re-pulled on the window's capped refresh.</param>
/// <param name="OnRightClick">Optional: invoked when the row is right-clicked (CombatMeter row context menu); null = no menu.</param>
public sealed record MeterRowElement(System.Func<MeterRowData> Data, System.Action? OnRightClick = null) : HudElement
{
    /// <summary>Visual style of the row. <see cref="MeterRowStyle.Classic"/> (the default) is the original flat
    /// meter row, so every plugin built before this property existed renders byte-identical.
    /// <see cref="MeterRowStyle.Rounded"/> opts in to the Stellar Design Language look (rounded theme tile, rounded
    /// spine/bar, accent self ring, recessed empty slot). Chosen once when the row is built. Init property, NOT a
    /// ctor param, so the record's primary constructor stays binary-compatible.</summary>
    public MeterRowStyle Style { get; init; }
}

/// <summary>Visual style of a <see cref="MeterRowElement"/>.</summary>
public enum MeterRowStyle
{
    /// <summary>The original flat meter row (dark backing, square self/talk borders, square spine and bar).</summary>
    Classic = 0,
    /// <summary>House-style rounded row: a radius-6 theme Tile (theme-live, fades with the window opacity), a
    /// rounded spine inset 4 px, a radius-5 bar on a recessed track, rounded status-effect cells, the self row as a
    /// 20 % accent wash plus a 1.5 px accent ring (instead of <see cref="MeterRowData.SelfAccent"/>), the
    /// <see cref="MeterRowData.RowBorder"/> colour as a rounded ring, a faded dead row, and
    /// <see cref="MeterRowData.IsEmptySlot"/> rows drawn as a recessed tile with an optional
    /// <see cref="MeterRowData.EmptyHint"/>.</summary>
    Rounded = 1,
}
