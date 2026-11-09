using System;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Thai on a legacy uGUI <see cref="Text"/> drawn through the TMP path in the shipped StellarUIThai face (the one the
/// bold/Emphasis text already uses), with GPOS mark positioning — instead of the OS family list, whose "Noto Sans Thai"
/// is not installed on stock Windows and falls back to looped Tahoma / Leelawadee (two Thai styles side by side).
/// Legacy Text can't take the shipped TTF (<c>new Font(path)</c> draws nothing on it), so the text moves to TMP.
/// <para>How: a lazily built TMP SIBLING ("twin") of the legacy Text, created the first time a Thai string reaches
/// it. Its GameObject replaces the legacy one in the layout (legacy SetActive(false), twin SetActive(true)) at the
/// same sibling slot with a copy of its RectTransform, LayoutElement (fixed width, pinned Height, flex) and
/// RectMask2D, so the layout contract is identical; only the glyph metrics are the TMP face's own. A later non-Thai
/// string swaps back, so a live language switch (windows re-skin in place, Func labels re-poll — no rebuild) and a
/// Thai name inside a Latin UI both land right. The legacy Text stays the STYLE MODEL: every existing colour /
/// size writer (bindings, reskin closures, hover tints) keeps writing it while it is hidden, and <see cref="Sync"/>
/// mirrors those values onto the twin — so no writer had to learn about TMP.</para>
/// <para>Cost: nothing for a text that never shows Thai (one <see cref="GlyphScript.IsThai"/> char scan per
/// string CHANGE; no object until then). A Thai text costs one extra GameObject + TextMeshProUGUI (the hidden legacy
/// Text stays allocated), TMP mesh/layout instead of uGUI's, and per apply a colour/size compare + FitToRect's
/// width read. Each Thai↔non-Thai swap dirties the parent layout once.</para>
/// Shared with the Mono UI-sandbox (TMP-free): with no <see cref="StyledTextFactory.CreateBold"/> nothing is built.
/// </summary>
internal sealed class ThaiTmpTwin
{
    private readonly Text _legacy;
    private readonly IStyledTextHandle _h;
    private Color _color;
    private int _size;

    private ThaiTmpTwin(Text legacy, IStyledTextHandle h)
    {
        _legacy = legacy; _h = h;
        _color = legacy.color; _size = legacy.fontSize;
    }

    /// <summary>True while the twin (not the legacy Text) is the laid-out, drawn element.</summary>
    public bool Showing { get; private set; }

    /// <summary>True when the shown element is active in the hierarchy (the bindings' hidden-row skip).</summary>
    public bool ActiveInHierarchy
    {
        get
        {
            var go = Showing ? _h.Go : (_legacy == null ? null : _legacy.gameObject);
            return go != null && go.activeInHierarchy;
        }
    }

    /// <summary>Show <paramref name="s"/> on the right element: a Thai string on the twin (built on first use into
    /// <paramref name="twin"/>), anything else back on the legacy Text. True = the twin shows it (the caller must not
    /// write the legacy Text); false = the caller sets the legacy Text as before. Also false when the twin can't be
    /// built (sandbox / no shipped face / build throw) — the legacy Text then keeps showing Thai, as before.
    /// <paramref name="ellipsis"/> / <paramref name="maxLines"/> = the legacy binding's fit mode, done by TMP's own
    /// cut (TmpStyledText.Ellipsis / .MaxLines) so the cut is measured in the face that draws it.</summary>
    public static bool Route(ref ThaiTmpTwin? twin, Text legacy, string? s, bool ellipsis = false, int maxLines = 0)
    {
        if (!GlyphScript.IsThai(s))
        {
            twin?.Show(false);
            return false;
        }
        twin ??= Create(legacy, s!, ellipsis, maxLines);
        if (twin == null) return false;
        twin._h.SetText(s!);
        twin.Show(true);
        twin.Sync();
        return true;
    }

    /// <summary>A static label (set once at build, e.g. a dropdown item): route it once; no binding follows. The
    /// twin takes the legacy colour/size at build time, which is all a static label ever has.</summary>
    public static void RouteStatic(Text legacy, string? s)
    {
        ThaiTmpTwin? twin = null;
        Route(ref twin, legacy, s);
    }

    /// <summary>Per apply while showing: mirror the legacy Text's colour + size (written by bindings / reskin / hover
    /// while it is hidden) and let an Ellipsis / MaxLines twin re-cut to a changed rect width.</summary>
    public void Sync()
    {
        if (!Showing || _legacy == null) return;
        var c = _legacy.color;
        if (c != _color) { _color = c; _h.SetColor(c); }
        var px = _legacy.fontSize;
        if (px != _size) { _size = px; _h.SetFontSize(px); }
        _h.FitToRect();
    }

    private void Show(bool twin)
    {
        if (Showing == twin || _legacy == null) return;
        var go = _h.Go;
        if (go == null) return;
        Showing = twin;
        // Twin first on the way in, legacy first on the way out: the slot is never empty for a layout pass.
        if (twin) { go.SetActive(true); _legacy.gameObject.SetActive(false); }
        else { _legacy.gameObject.SetActive(true); go.SetActive(false); }
    }

    private static ThaiTmpTwin? Create(Text legacy, string s, bool ellipsis, int maxLines)
    {
        var create = StyledTextFactory.CreateBold;
        if (create == null || legacy == null) return null;
        var parent = legacy.transform.parent;
        if (parent == null) return null;
        var before = parent.childCount;
        try
        {
            var h = create(parent, new StyledTextSpec(s, legacy.fontSize, legacy.color,
                wrap: legacy.horizontalOverflow == HorizontalWrapMode.Wrap && maxLines <= 0)
            {
                // Same weight as the legacy text (FontStyle.Bold / BoldAndItalic): the regular face for regular text.
                Bold = (legacy.fontStyle & FontStyle.Bold) != 0,
                Align = AlignOf(legacy.alignment),
                Ellipsis = ellipsis && maxLines <= 0,
                MaxLines = maxLines,
            });
            var go = h?.Go;
            if (h == null || go == null) return null;
            go.name = "ThaiText";
            go.transform.SetSiblingIndex(legacy.transform.GetSiblingIndex() + 1);
            CopyRect(legacy, go);
            CopyLayout(legacy, go, h, ellipsis || maxLines > 0);   // measures the full string: while still active
            go.SetActive(false);   // Show(true) swaps it in
            return new ThaiTmpTwin(legacy, h);
        }
        catch (Exception ex)
        {
            // A half-built twin must not stay in the layout next to the legacy Text.
            for (var i = parent.childCount - 1; i >= before; i--)
                UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
            StyledTextFactory.Log?.Invoke($"[Window] Thai TMP twin failed, keeping legacy Text: {ex.GetType().Name}");
            return null;
        }
    }

    // Anchors/pivot/offsets: a label anchored (not laid out by a group) must sit exactly where the legacy one did.
    // GetComponent<RectTransform>, not a cast: IL2CPP transform casts are unreliable (KB "transform.parent is
    // RectTransform is false on IL2CPP").
    private static void CopyRect(Text legacy, GameObject go)
    {
        var src = legacy.rectTransform;
        var rt = go.GetComponent<RectTransform>();
        if (src == null || rt == null) return;
        rt.anchorMin = src.anchorMin; rt.anchorMax = src.anchorMax; rt.pivot = src.pivot;
        rt.anchoredPosition = src.anchoredPosition; rt.sizeDelta = src.sizeDelta;
    }

    // The legacy GO's LayoutElement (TextElement.Width / Height pins, minWidth=0, flexibleWidth=0, tile widths) and
    // its clip mask (fixed-width NoWrap / Ellipsize) carry over unchanged. A fit-mode twin with a NATURAL width lets
    // the handle own preferredWidth = the full string padded — the same contract TryBuildEmphasisText sets up.
    private static void CopyLayout(Text legacy, GameObject go, IStyledTextHandle h, bool fits)
    {
        var src = legacy.GetComponent<LayoutElement>();
        if (src != null || fits)
        {
            var le = go.AddComponent<LayoutElement>();
            if (src != null)
            {
                le.ignoreLayout = src.ignoreLayout; le.layoutPriority = src.layoutPriority;
                le.minWidth = src.minWidth; le.preferredWidth = src.preferredWidth; le.flexibleWidth = src.flexibleWidth;
                le.minHeight = src.minHeight; le.preferredHeight = src.preferredHeight; le.flexibleHeight = src.flexibleHeight;
            }
            if (fits && le.preferredWidth < 0f) h.PadPreferredWidth(le);
        }
        if (legacy.GetComponent<RectMask2D>() != null) go.AddComponent<RectMask2D>();
    }

    private static TextAlign AlignOf(TextAnchor a) => a switch
    {
        TextAnchor.UpperCenter or TextAnchor.MiddleCenter or TextAnchor.LowerCenter => TextAlign.Center,
        TextAnchor.UpperRight or TextAnchor.MiddleRight or TextAnchor.LowerRight => TextAlign.Right,
        _ => TextAlign.Left,
    };
}
