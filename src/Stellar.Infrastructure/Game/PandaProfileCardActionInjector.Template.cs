using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reads a native sibling action button (<c>btn_idcard</c>) to capture how the game styles its own
/// action icons + labels, so <see cref="PandaProfileCardActionInjector"/> can MIRROR that look onto its
/// injected buttons (tint + size + anchors) instead of guessing. The native icons are a muted grey-white
/// at a specific footprint; copying the sibling's real values keeps our magnifier from looking too bright
/// or the wrong size. When a node can't be located the corresponding capture is left absent and the caller
/// falls back to its explicit layout.
/// </summary>
internal sealed partial class PandaProfileCardActionInjector
{
    // A captured RectTransform geometry + Graphic tint from a native node. Used to mirror the native
    // icon/label onto our injected equivalents. Null fields => couldn't read => caller uses its fallback.
    private readonly struct NodeStyle
    {
        public readonly bool Found;
        public readonly Vector2 SizeDelta;
        public readonly Vector2 AnchorMin;
        public readonly Vector2 AnchorMax;
        public readonly Vector2 Pivot;
        public readonly Vector2 AnchoredPosition;
        public readonly Color Color;

        public NodeStyle(RectTransform rt, Color color)
        {
            Found = true;
            SizeDelta = rt.sizeDelta;
            AnchorMin = rt.anchorMin;
            AnchorMax = rt.anchorMax;
            Pivot = rt.pivot;
            AnchoredPosition = rt.anchoredPosition;
            Color = color;
        }

        // Apply the captured geometry onto a freshly-built node's RectTransform.
        public void ApplyTo(RectTransform rt)
        {
            rt.anchorMin = AnchorMin;
            rt.anchorMax = AnchorMax;
            rt.pivot = Pivot;
            rt.sizeDelta = SizeDelta;
            rt.anchoredPosition = AnchoredPosition;
        }
    }

    // The native icon + label styling captured from a sibling button, plus the sibling's TMP label source
    // (for font/material copy — geometry comes from LabelStyle). Passed to BuildButton's icon/label adders.
    private readonly struct NativeActionTemplate
    {
        public readonly NodeStyle IconStyle;
        public readonly NodeStyle LabelStyle;
        public readonly TMP_Text? LabelSource;

        public NativeActionTemplate(NodeStyle iconStyle, NodeStyle labelStyle, TMP_Text? labelSource)
        {
            IconStyle = iconStyle;
            LabelStyle = labelStyle;
            LabelSource = labelSource;
        }
    }

    // Read the sibling button's ICON node and LABEL node, capturing tint + geometry from each. The LABEL is
    // the TMP_Text we already copy font from; we now also read its colour + rect. The ICON is the first
    // Graphic descendant that is neither the button's own background nor the label and looks like an icon
    // (name hints, or simply a sprite/texture-bearing Image/RawImage that isn't the root). Either capture may
    // be absent (Found=false) — the caller then falls back to its explicit layout.
    private NativeActionTemplate CaptureNativeTemplate(Transform sibling)
    {
        var label = sibling.GetComponentInChildren<TMP_Text>(includeInactive: true);
        var labelStyle = label != null && label.rectTransform != null
            ? new NodeStyle(label.rectTransform, label.color)
            : default;

        var iconStyle = CaptureIconStyle(sibling, label);
        DiagNativeTemplate(iconStyle, labelStyle);
        return new NativeActionTemplate(iconStyle, labelStyle, label);
    }

    // Pick the native ICON graphic under the sibling. Heuristic, in priority order:
    //   1. a Graphic whose GameObject name suggests an icon ("icon"/"img", not "bg") and bears art;
    //   2. otherwise the first Image/RawImage (not the root, not the label) that carries a sprite/texture.
    // Returns an absent NodeStyle when none qualifies.
    private static NodeStyle CaptureIconStyle(Transform sibling, TMP_Text? label)
    {
        var graphics = sibling.GetComponentsInChildren<Graphic>(includeInactive: true);
        Graphic? fallback = null;
        for (var i = 0; i < graphics.Length; i++)
        {
            var g = graphics[i];
            if (g == null || g.transform == sibling) continue;     // skip the button's own background root
            if (label != null && g.gameObject == label.gameObject) continue;   // skip the label
            if (!HasArt(g)) continue;
            if (NameSuggestsIcon(g.gameObject.name))
            {
                var rt = g.rectTransform;
                if (rt != null) return new NodeStyle(rt, g.color);
            }
            fallback ??= g;
        }
        if (fallback != null && fallback.rectTransform != null) return new NodeStyle(fallback.rectTransform, fallback.color);
        return default;
    }

    // A Graphic "bears art" if it's an Image with a sprite or a RawImage with a texture (a transparent
    // hit-surface / colour swatch carries none and is rejected as an icon candidate).
    private static bool HasArt(Graphic g) =>
        (g is Image img && img.sprite != null) || (g is RawImage raw && raw.texture != null);

    // Name hints for an icon node: contains "icon" or "img" and not "bg" (background).
    private static bool NameSuggestsIcon(string name) =>
        (name.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("img", StringComparison.OrdinalIgnoreCase) >= 0)
        && name.IndexOf("bg", StringComparison.OrdinalIgnoreCase) < 0;
}
