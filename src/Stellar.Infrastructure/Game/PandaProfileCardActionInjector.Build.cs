using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Per-action button construction for <see cref="PandaProfileCardActionInjector"/>. Builds one
/// no-box button (transparent click surface + icon-over-label) matching the native action buttons,
/// sized to a sibling cell. The icon is the spec's <c>IconPng</c> rasterised via
/// <see cref="UI.PluginIconCache"/> (label-only when null).
/// </summary>
internal sealed partial class PandaProfileCardActionInjector
{
    // BUILD one action button from scratch (not a clone): a raycastable transparent Image surface + an
    // optional icon RawImage + a TMP label, sized to the sibling cell with a LayoutElement so a layout
    // group on the row doesn't collapse/overlap it. Appended last-sibling (Tick re-asserts order).
    private GameObject BuildButton(Transform layout, Vector2 size, Transform sibling, ProfileCardActionSpec spec, string name)
    {
        var template = CaptureNativeTemplate(sibling);
        var go = NewButtonObject(layout, size, name);
        AddRaycastSurface(go);
        AddIcon(go.transform, spec, template.IconStyle, template.LabelStyle);
        AddLabel(go.transform, spec.Label, template);
        return go;
    }

    // New from-scratch GameObject under the action row, sized to the sibling cell and given a LayoutElement
    // (preferred width/height) so a HorizontalLayoutGroup on layout_interactive sizes it like the native
    // cells instead of collapsing it to 0 or letting it overlap a neighbour.
    private static GameObject NewButtonObject(Transform layout, Vector2 size, string name)
    {
        var go = new GameObject(name);
        go.AddComponent<CanvasRenderer>();
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(layout, worldPositionStays: false);
        rt.localScale = Vector3.one;
        rt.sizeDelta = size;
        go.transform.SetAsLastSibling();

        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = size.x;
        le.preferredHeight = size.y;
        le.flexibleWidth = 0f;
        return go;
    }

    // The click surface: a FULLY TRANSPARENT Image. The native action buttons draw no box — just an icon
    // over a label — so we match by drawing no background. raycastTarget stays TRUE (uGUI raycasts a
    // transparent Image fine); the manual hit-test reads the RectTransform regardless.
    private static void AddRaycastSurface(GameObject go)
    {
        var img = go.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0f);   // fully transparent — no visible box
        img.raycastTarget = true;
    }

    // The action ICON, mirroring the native sibling icon's tint + geometry so our magnifier matches the game
    // (muted grey-white, same footprint), instead of the old hardcoded white/0.8 + 44px. A RawImage drawing
    // the spec's IconPng rasterised by PluginIconCache (mip/trilinear/HideAndDontSave). raycastTarget off (the
    // surface owns clicks). No icon when IconPng is null/undecodable. Falls back to the explicit upper-band
    // layout when the native icon node couldn't be read; falls back to the label's colour if only the icon
    // GEOMETRY was found but its tint is unreliable (label colour is the same muted grey).
    private void AddIcon(Transform parent, ProfileCardActionSpec spec, NodeStyle iconStyle, NodeStyle labelStyle)
    {
        var tex = _icons.Get(spec.IconPng);
        if (tex == null) return;   // label-only button

        var go = new GameObject("Icon");
        go.transform.SetParent(parent, worldPositionStays: false);
        var rt = go.AddComponent<RectTransform>();
        // Our procedural magnifier glyph fills its texture edge-to-edge, whereas the native sprites carry
        // internal padding — so at the SAME rect size ours reads visually larger. Shrink to match.
        const float iconScale = 0.8f;
        if (iconStyle.Found)
        {
            iconStyle.ApplyTo(rt);   // mirror native size + anchors + position
            rt.sizeDelta *= iconScale;
        }
        else
        {
            // Fallback: explicit centred upper-band layout, fixed pixel size.
            rt.anchorMin = new Vector2(0.5f, 0.7f);
            rt.anchorMax = new Vector2(0.5f, 0.7f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(IconPx, IconPx) * iconScale;
        }

        var raw = go.AddComponent<RawImage>();
        raw.texture = tex;
        raw.color = ResolveIconTint(iconStyle, labelStyle);
        raw.raycastTarget = false;
    }

    // The native icon tint to copy: the icon node's own colour when read; else the label colour (same muted
    // grey the labels use); else the legacy muted-white as a last resort.
    private static Color ResolveIconTint(NodeStyle iconStyle, NodeStyle labelStyle)
    {
        if (iconStyle.Found) return iconStyle.Color;
        if (labelStyle.Found) return labelStyle.Color;
        return new Color(1f, 1f, 1f, 0.8f);
    }

    // The label, copying the sibling button's TMP font/material/size/colour so it reads like a native action,
    // and mirroring the native label node's RectTransform (anchors/size/position) when it was captured —
    // otherwise the explicit lower-band layout. Falls back to a uGUI Text if the sibling exposes no TMP label.
    // raycastTarget off (the surface owns clicks).
    private static void AddLabel(Transform parent, string text, NativeActionTemplate template)
    {
        var src = template.LabelSource;
        var go = new GameObject("Label");
        go.transform.SetParent(parent, worldPositionStays: false);
        var rt = go.AddComponent<RectTransform>();
        ApplyLabelGeometry(rt, template.LabelStyle);

        if (src != null)
        {
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = src.font; t.fontSharedMaterial = src.fontSharedMaterial; t.color = src.color;
            t.fontSize = src.fontSize; t.fontStyle = src.fontStyle;
            t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false; t.text = text;
            return;
        }
        var ut = go.AddComponent<Text>();
        ut.alignment = TextAnchor.MiddleCenter; ut.fontSize = 18; ut.color = Color.white; ut.raycastTarget = false;
        try { ut.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { /* box still shows */ }
        ut.text = text;
    }

    // Mirror the native label's RectTransform when captured; else the explicit lower-band (bottom 40%) layout.
    private static void ApplyLabelGeometry(RectTransform rt, NodeStyle labelStyle)
    {
        if (labelStyle.Found)
        {
            labelStyle.ApplyTo(rt);
            return;
        }
        rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0.4f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }
}
