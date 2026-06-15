using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>Base for a declarative mod-uGUI element. Unity-free so it lives in Abstractions.</summary>
public abstract record NativeUiElementSpec(NativeUiAnchor Anchor);

/// <summary>A native-styled button injected at <paramref name="Anchor"/>.</summary>
/// <remarks>
/// <paramref name="IconPng"/> (raw PNG bytes — the plugin reads its embedded image)
/// is the preferred icon source: it rasterises to a real sprite that looks native
/// in-world. <paramref name="IconKey"/> is the font-glyph fallback (glyphs tofu on
/// the game's rail font, so prefer the PNG). <paramref name="IconPng"/> wins when both
/// are supplied. Kept as <see cref="byte"/>[] so Abstractions stays Unity-free.
/// </remarks>
public sealed record MenuButtonSpec(
    NativeUiAnchor Anchor, string Label, string? IconKey, string? Tooltip, Action OnClick,
    byte[]? IconPng = null)
    : NativeUiElementSpec(Anchor);

/// <summary>A read-only text indicator; <paramref name="OnUpdate"/> is re-pulled each refresh.</summary>
public sealed record IndicatorSpec(
    NativeUiAnchor Anchor, Func<string> OnUpdate, ColorRgba? Tint = null)
    : NativeUiElementSpec(Anchor);

/// <summary>A themed panel containing declarative child widgets.</summary>
public sealed record PanelSpec(
    NativeUiAnchor Anchor, IReadOnlyList<PanelWidget> Children)
    : NativeUiElementSpec(Anchor);

/// <summary>Declarative panel child widgets.</summary>
public abstract record PanelWidget;
/// <summary>Static text label child widget for a <see cref="PanelSpec"/>.</summary>
/// <param name="Text">Text content to display.</param>
/// <param name="Tint">Optional tint colour; null uses the framework default text colour.</param>
public sealed record LabelWidget(string Text, ColorRgba? Tint = null) : PanelWidget;

/// <summary>Key/value row child widget for a <see cref="PanelSpec"/>; value is re-pulled each refresh.</summary>
/// <param name="Key">Static label displayed on the left.</param>
/// <param name="Value">Dynamic value factory displayed on the right; re-pulled each refresh.</param>
public sealed record ValueRowWidget(string Key, Func<string> Value) : PanelWidget;

/// <summary>Horizontal fill bar child widget for a <see cref="PanelSpec"/>.</summary>
/// <param name="Fraction01">Dynamic fill fraction in [0..1]; re-pulled each refresh.</param>
/// <param name="Fill">Optional fill colour; null uses the framework default bar fill colour.</param>
public sealed record BarWidget(Func<float> Fraction01, ColorRgba? Fill = null) : PanelWidget;
