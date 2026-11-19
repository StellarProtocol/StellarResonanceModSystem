using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Draws layers ON TOP of each other in the same rect (framework 2.33.0) — e.g. a "Loading…" overlay over a chart and
/// table. <paramref name="Base"/> is laid out like any other child and defines the stack's size; each of the
/// <paramref name="Overlays"/> is stretched over that rect (it never affects layout) and drawn above the base and above
/// every earlier overlay, in order.
/// <para>Overlays are built once, with the window. Showing or hiding one (<see cref="StackLayer.Visible"/>, polled at the
/// window refresh rate) only switches it on/off — the base is not rebuilt or re-laid-out.</para>
/// <para>Input: a visible overlay with <see cref="StackLayer.BlocksInput"/> swallows clicks, hover, the mouse wheel and
/// drags over its rect — nothing in the layers below it reacts. Without it the overlay is decorative and fully
/// click-through (its own buttons don't react either). A hidden overlay never blocks anything.</para>
/// <para>Sizing follows <see cref="ConditionalElement"/>: in a Column the stack fills the width; in a Row it takes the
/// base's preferred width (it does not grab leftover row space); leftover height is shared only if the base asks for it.</para>
/// </summary>
/// <param name="Base">Layer 0: the content underneath (laid out normally).</param>
/// <param name="Overlays">Layers 1..n, drawn over the base in order (last = top).</param>
public sealed record LayerStackElement(HudElement Base, params StackLayer[] Overlays) : HudElement;

/// <summary>One overlay of a <see cref="LayerStackElement"/>. Not an element on its own — only valid inside a stack.
/// Every option is an <c>init</c> property, so more can be added later without breaking compiled plugins.</summary>
/// <param name="Content">What the overlay shows. By default it is stretched to cover the whole stack rect.</param>
public sealed record StackLayer(HudElement Content)
{
    /// <summary>Shown while this returns true (polled at the window refresh rate; a change only toggles the overlay on or
    /// off). Null (default) = always shown.</summary>
    public Func<bool>? Visible { get; init; }

    /// <summary>True = while shown, the overlay swallows ALL pointer input over its rect (click, hover, wheel, drag), so
    /// nothing in the layers below reacts — the window can still be moved by its frame as usual. False (default) = purely
    /// decorative: the overlay and everything in it is click-through.</summary>
    public bool BlocksInput { get; init; }

    /// <summary>Optional fill painted over the whole overlay rect, behind <see cref="Content"/> (e.g. a translucent dim
    /// over the base). Polled at the window refresh rate and repainted only when it changes, so it can follow the theme
    /// live; the alpha is honoured. Null (default) = no fill.</summary>
    public Func<ColorRgba>? Background { get; init; }

    /// <summary>True = <see cref="Content"/> keeps its preferred size and is centred in the overlay (a spinner, a
    /// "Loading…" label). False (default) = the content is stretched to fill the overlay.</summary>
    public bool Center { get; init; }

    /// <summary>Place the overlay in a sub-rect of the stack instead of covering all of it (framework 2.34.0), in px
    /// from the stack's edges — e.g. a bar or a label over a slot of frame art. The rect follows the stack's size (a
    /// window resize) with no per-refresh work. Input blocking and <see cref="Background"/> use the placed rect. Null
    /// (default) = cover the whole stack, unchanged.</summary>
    public LayerRect? Rect { get; init; }
}

/// <summary>Where a <see cref="StackLayer"/> sits inside its stack (framework 2.34.0). Per axis: both insets set =
/// stretch between them; only the near inset (<see cref="Left"/> / <see cref="Top"/>) = anchored to that edge with the
/// fixed <see cref="Width"/> / <see cref="Height"/> (0 = stretch to the far edge); only the far inset
/// (<see cref="Right"/> / <see cref="Bottom"/>) = anchored to that edge with the fixed size; neither = the whole axis.</summary>
public sealed record LayerRect
{
    /// <summary>Inset from the stack's left edge, px.</summary>
    public float? Left { get; init; }
    /// <summary>Inset from the stack's right edge, px.</summary>
    public float? Right { get; init; }
    /// <summary>Inset from the stack's top edge, px.</summary>
    public float? Top { get; init; }
    /// <summary>Inset from the stack's bottom edge, px.</summary>
    public float? Bottom { get; init; }
    /// <summary>Fixed width when only one horizontal inset is set; 0 = stretch.</summary>
    public float Width { get; init; }
    /// <summary>Fixed height when only one vertical inset is set; 0 = stretch.</summary>
    public float Height { get; init; }
}
