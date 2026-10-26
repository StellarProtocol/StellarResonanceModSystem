using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// TextElement.Ellipsize on the TMP path: the framework cuts the string itself instead of using TMP's Ellipsis
// overflow mode. In-game (2026-10-25, ChatTools whisper list) a one-word bold name in a slot laid out at exactly
// its preferred width rendered NOTHING once the pane re-activated: TMP judged the exact fit as overflowing (float
// noise) and its Ellipsis pass then dropped the whole line (characterCount 0, isTextOverflowing true, survives
// ForceMeshUpdate) instead of drawing "L…". Both shipped faces DO carry U+2026 (checked in the TTF cmaps), so the
// glyph-missing → Truncate fallback is not proven to be the cause; either way the mode can wipe a single-word line,
// so it is not used. Here: overflowMode stays Overflow (never drops glyphs), the slot's preferred width is padded
// (round up + 1) so a slot with room is never an exact fit, and a narrower slot gets a binary-searched prefix + "…".
// Cost: TMP preferred sizes are NOT cached (full layout per read — WindowBuilder-Patterns.md), so text is measured
// only when the string, the font size or the rect width changes; the per-apply poll reads rect.width alone.
internal sealed partial class TmpStyledText
{
    private readonly bool _ellipsis;
    private string _full = "";        // the uncut string (the shown text may be a cut)
    private float _naturalW;          // _full's preferred width at the current face/size
    private float _fitW = -1f;        // rect width the shown text was cut for (-1 = re-fit on the next poll)
    private LayoutElement? _padLe;    // owned preferred width (null = fixed-Width element, layout is the caller's)

    // Per font asset: does U+2026 resolve (primary + fallbacks, adding it to the dynamic atlas)? Checked once per face;
    // a face without it gets ASCII "..." (every face has '.') rather than a blank/tofu ellipsis.
    private static readonly Dictionary<int, bool> EllipsisGlyph = new();

    public void PadPreferredWidth(LayoutElement le)
    {
        _padLe = le;
        WritePad();
    }

    public void FitToRect()
    {
        if (!_ellipsis || _t == null) return;
        var w = _t.rectTransform.rect.width;
        if (Mathf.Abs(w - _fitW) <= 0.5f) return;
        _fitW = w;
        // Not laid out yet (0) or room for the whole string → the full string; the first real width re-fits it.
        // The setter is a no-op when the string is unchanged, so this re-assigns only a real change.
        var cut = !(w <= 1f || _naturalW <= w);
        _t.text = cut ? Cut(w) : _full;
        EllipsisDiag(w, cut);   // TEMP [EllipsisDiag] — remove before release
    }

    private void SetEllipsized(string s)
    {
        _full = s ?? "";
        MeasureAndFit();
    }

    private void MeasureAndFit()
    {
        _t.text = _full;
        _naturalW = _t.preferredWidth;
        WritePad();
        _fitW = -1f;
        RelayoutForNewString();
        FitToRect();
    }

    // The rect still has the width laid out for the PREVIOUS string: WritePad only dirties the layout, and uGUI rebuilds
    // it at the end of the frame — after this cut is drawn, and the next re-fit is a whole apply (~10 Hz) away. In a
    // recycled virtual-list row that showed up in-game as names that fit at rest flashing cut while scrolling ("Welll"
    // cut to "W…" in a slot that had held a shorter name; ChatTools whisper list, 2026-10-26). So when the new string
    // would be cut, lay out the parent group now, so the cut uses the width the slot actually gets this frame. A
    // string that fits needs no cut and no rebuild, which keeps this off the common path (TMP measures are uncached).
    //
    // ⚠️ IL2CPP: the first version (0360b92) guarded with `transform.parent is not RectTransform parent`. On interop
    // wrappers that CLR type-check is unreliable: `parent` comes back as a plain `Transform` wrapper unless a
    // RectTransform wrapper for the same native object happens to still be alive in Il2CppInterop's weak object pool
    // (it is right after the build, gone after a GC). So in practice the guard returned early and the rebuild never
    // ran — the flash survived the "fix". TryCast asks the native type instead (same trap as `is TMP_Text`,
    // WindowBuilder-Patterns.md).
    private void RelayoutForNewString()
    {
        _diagRelayout = null;   // TEMP [EllipsisDiag]
        if (_padLe == null || _naturalW <= _t.rectTransform.rect.width + 0.5f) return;
        var root = LayoutRootFor(_t.transform);
        if (root == null) { _diagRelayout = "skip(no group)"; return; }
        try { LayoutRebuilder.ForceRebuildLayoutImmediate(root); _diagRelayout = root.name; }
        catch { _diagRelayout = "skip(threw)"; /* mid-destroy: the next apply re-fits from the normal layout pass */ }
    }

    // The group to rebuild: the label's parent group, climbed while that group is itself CONTENT-sized by its parent
    // (a Row in a Row, or a Column that doesn't force-expand) — rebuilding such a group alone would re-split its own
    // stale width. Stops at the first group whose width is handed down from above (a force-expanding Column, a Cell,
    // a list slot / layout root), which is the width the cut must fit. Whisper row: name → its Row (under a
    // force-expand Column) = one small group, so the extra cost stays a couple of TMP measures. Capped at 4 hops.
    private static RectTransform? LayoutRootFor(Transform label)
    {
        var p = label.parent;
        if (p == null || p.GetComponent<LayoutGroup>() == null) return null;
        for (var hops = 0; hops < 4; hops++)
        {
            var up = p.parent;
            if (up == null) break;
            var g = up.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (g == null) break;   // p is a layout root (list slot, window content) — its width is fixed from outside
            var contentSized = g.TryCast<HorizontalLayoutGroup>() != null || !g.childForceExpandWidth;
            if (!contentSized) break;
            p = up;
        }
        return p.TryCast<RectTransform>();
    }

    // Ceil + 1: the laid-out width can come back a hair under the measured width after a re-layout (2-decimal rect,
    // TMP's own rounding), which used to flip a fitting name to "overflowing". minWidth stays 0, so it still shrinks.
    private void WritePad()
    {
        if (_padLe == null) return;
        var pw = Mathf.Ceil(_naturalW) + 1f;
        if (Mathf.Abs(_padLe.preferredWidth - pw) > 0.01f) _padLe.preferredWidth = pw;
    }

    // Longest prefix whose prefix + ellipsis fits w (~log2 n measurements). Each probe is a real text set + one TMP
    // layout; GetPreferredValues(string) is avoided because it overwrites TMP's parse buffers without dirtying, and
    // the text setter's same-string early-out would then leave the probe on screen. Nothing fits → just the ellipsis,
    // or empty when even that is wider than the slot.
    private string Cut(float w)
    {
        var ell = EllipsisFor(_t.font);
        var best = Measure(ell) <= w ? ell : "";
        int lo = 1, hi = _full.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var c = Prefix(mid) + ell;
            if (Measure(c) <= w) { best = c; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }

    private float Measure(string s)
    {
        _t.text = s;
        return _t.preferredWidth;
    }

    // Never split a surrogate pair or leave a half-open rich-text tag ("<colo" would draw literally); trailing
    // whitespace dropped so the ellipsis hugs the last glyph.
    private string Prefix(int n)
    {
        if (n > 0 && char.IsHighSurrogate(_full[n - 1])) n--;
        var p = _full.Substring(0, n);
        var lt = p.LastIndexOf('<');
        if (lt >= 0 && p.IndexOf('>', lt) < 0) p = p.Substring(0, lt);
        return p.TrimEnd();
    }

    private static string EllipsisFor(TMP_FontAsset? face)
    {
        if (face == null) return "...";
        var id = face.GetInstanceID();
        if (EllipsisGlyph.TryGetValue(id, out var has)) return has ? "…" : "...";
        try { has = face.HasCharacter('…', true, true); }
        catch { has = false; }
        EllipsisGlyph[id] = has;
        StyledTextFactory.Log?.Invoke($"[StyledText] ellipsis U+2026 in '{face.name}': {(has ? "yes" : "no, using ...")}");
        return has ? "…" : "...";
    }

    // ── TEMP [EllipsisDiag] — remove before release (fix/virtuallist-rebind-flicker in-game confirmation) ──────────
    // One line per fit that CUT or that ran a relayout (fits that show the whole string without a relayout are the
    // common case and stay silent). Rate-limited to DiagPerSec lines per real-time second across all labels.
    private string? _diagRelayout;   // what RelayoutForNewString did on the current string change (null = not asked)
    private static float _diagWindowStart = -1f;
    private static int _diagInWindow;
    private const int DiagPerSec = 30;

    private void EllipsisDiag(float w, bool cut)
    {
        var relayout = _diagRelayout;
        _diagRelayout = null;   // a later width-only re-fit logs "-" (that is the ~10 Hz self-correction)
        if (!cut && relayout == null) return;
        var log = StyledTextFactory.Log;
        if (log == null) return;
        var now = Time.realtimeSinceStartup;
        if (now - _diagWindowStart >= 1f) { _diagWindowStart = now; _diagInWindow = 0; }
        if (++_diagInWindow > DiagPerSec) return;
        var p = _t.transform.parent;
        var prt = p != null ? p.TryCast<RectTransform>() : null;
        var pw = prt != null ? prt.rect.width : -1f;
        log($"[EllipsisDiag] f={Time.frameCount} '{_full}' natural={_naturalW:F1} rectW={w:F1} parentW={pw:F1} " +
            $"cut={(cut ? "'" + _t.text + "'" : "no")} relayout={relayout ?? "-"}");
    }
}
