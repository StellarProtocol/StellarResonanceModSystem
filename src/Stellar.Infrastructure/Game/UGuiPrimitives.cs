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
}
