using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Shared low-level uGUI construction helpers used by both <see cref="HudElementBuilder"/> (HUDs) and
/// <c>WindowBuilder</c> (interactive windows). Extracted (SP1 window-shell Plan 1, Task 5) so neither
/// builder duplicates layout/text plumbing and both stay under the 500-LoC file gate. No game/IL2CPP
/// deps — sandbox-pure. Bodies are verbatim from the original HudElementBuilder helpers.
/// </summary>
internal static class UGuiPrimitives
{
    // columns convention: RowMode(-1)=horizontal, ColumnMode(1)=vertical, >1=grid.
    public const int RowMode = -1;
    public const int ColumnMode = 1;

    public static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, worldPositionStays: false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);   // top-left
        rt.anchoredPosition = new Vector2(20f, -20f);                   // default until SetRect restores
        rt.localScale = Vector3.one;
        return rt;
    }

    public static GameObject NewChild(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, worldPositionStays: false);
        go.transform.localScale = Vector3.one;
        return go;
    }

    public static void AddLayout(GameObject go, float gap, int columns)
    {
        if (columns > 1)
        {
            var grid = go.AddComponent<GridLayoutGroup>();
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = columns;
            grid.spacing = new Vector2(gap, gap);
            grid.cellSize = new Vector2(120f, 34f);   // tuned in-game
            return;
        }
        var layout = columns == RowMode ? go.AddComponent<HorizontalLayoutGroup>() : (HorizontalOrVerticalLayoutGroup)go.AddComponent<VerticalLayoutGroup>();
        layout.spacing = gap;
        layout.childControlWidth = true; layout.childControlHeight = true;
        layout.childForceExpandWidth = false; layout.childForceExpandHeight = false;
        // Rows vertically-centre their children; columns/lists stay top-anchored.
        layout.childAlignment = columns == RowMode ? TextAnchor.MiddleLeft : TextAnchor.UpperLeft;
    }

    public static void ConfigureText(Text t, int fontSize, TextAnchor anchor, bool bold)
    {
        t.alignment = anchor; t.fontSize = fontSize;
        t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        try { t.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { /* box still shows */ }
    }

    // Emphasis weight for a specific string. Real bold ONLY for text the dynamic OS font can embolden
    // cleanly — a complex-script run (CJK/kana/Hangul/Thai) has no name-reachable bold face under
    // Proton/IL2CPP, so Unity's synthetic bold would mangle it (the i18n P0 JA/TH bold-header bug); those
    // stay FontStyle.Normal (the OS-font fallback chain renders the regular glyph correctly). Used by the
    // secondary emphasis paths (chart legend, HUD bar prefix); the window text + titles use the richer
    // script-aware path (Thai gets a real bold FACE — ApplyEmphasisFont / TextBinding).
    public static FontStyle EmphasisStyle(bool emphasis, string? text)
        => emphasis && !GlyphScript.HasSyntheticBoldRisk(text) ? FontStyle.Bold : FontStyle.Normal;

    public static void SetPreferred(GameObject go, float w, float h)
    {
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = w; le.preferredHeight = h;
    }

    public static void Stretch(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    // Trim with a trailing "..." so the string fits <paramref name="maxW"/> in the Text's current font (ASCII
    // dots — the "…" glyph tofus in-world). Measures via Text.preferredWidth; the caller only calls it when the
    // string changes. Returns the original when it already fits.
    public static string Ellipsize(Text t, string s, float maxW)
    {
        if (string.IsNullOrEmpty(s)) return s;
        t.text = s;
        if (t.preferredWidth <= maxW) return s;
        for (var len = s.Length - 1; len >= 1; len--)
        {
            t.text = s.Substring(0, len).TrimEnd() + "...";
            if (t.preferredWidth <= maxW) return t.text;
        }
        return "...";
    }

    /// <summary>The readability outline chrome-less overlays use (StatInspector's stat HUD, HUD-halo TextElements):
    /// a 4-direction dark halo behind the glyphs so light text reads over any background. <c>UnityEngine.UI.Shadow</c>
    /// is stripped from the game interop; <c>Outline</c> survives. The 1.1 px offset is deliberate — at exactly 1.0 px
    /// the four copies land pixel-aligned and a small bold glyph's halo reads as stair-stepped (owner, meter bar
    /// values 2026-09-09: "the text looks rough"); the sub-pixel offset lets the atlas sample bilinearly and softens it.</summary>
    public static Outline AddReadabilityOutline(GameObject go)
    {
        var ol = go.AddComponent<Outline>();
        ol.effectColor = new Color(0f, 0f, 0f, 0.85f);
        ol.effectDistance = new Vector2(1.1f, -1.1f);
        return ol;
    }

    /// <summary>
    /// Makes <paramref name="go"/> (the raycast target of a ticker-driven drag area — the XY pad, the colour picker's SV
    /// square and hue bar) CONSUME uGUI drag events, so a drag there no longer bubbles to an ancestor <c>ScrollRect</c> and
    /// scrolls the window (in-game 2026-10-05). The ticker reads the pointer itself (Input + rect hit-test), so the
    /// EventSystem only needs a drag handler to stop at — no IL2CPP interface injection.
    /// <para>The consumer is a NON-interactable <see cref="Slider"/> with no fill, handle or target graphic: Unity resolves
    /// the drag/press target by interface, so it receives OnInitializePotentialDrag/OnDrag/OnPointerDown, and every one of
    /// them returns at once (<c>MayDrag</c> = active AND interactable). Chosen over an inert <c>ScrollRect</c> (null
    /// content is inert too — <c>IsActive()</c> requires content) because a ScrollRect is also an <c>IScrollHandler</c>
    /// and would swallow the mouse wheel, so the window could not be wheel-scrolled over the pad; a Slider is not a scroll
    /// handler, so the wheel still bubbles to the window. Transition and navigation are off: no tint, never selected.</para>
    /// </summary>
    public static Slider AddDragSink(GameObject go)
    {
        var sink = go.AddComponent<Slider>();
        // Order matters: Selectable.Awake adopts the GameObject's Graphic as its target, and switching interactable
        // off while the ColorTint transition is still set would CrossFade that graphic to the disabled tint (half
        // alpha) — measured in the sandbox as a pad background dimmed from (55,70,76) to (47,62,69). Transition off
        // and target cleared first, then interactable off.
        sink.transition = Selectable.Transition.None;
        sink.targetGraphic = null;
        sink.interactable = false;
        sink.fillRect = null;
        sink.handleRect = null;
        var nav = sink.navigation;
        nav.mode = Navigation.Mode.None;
        sink.navigation = nav;
        return sink;
    }
}
