using UnityEngine;

namespace Stellar.Infrastructure.Game;

// TextElement.MaxLines on the TMP path (framework 2.30.0; first user = the launcher's two-line plugin names): the text
// wraps, and when it needs more than N lines at its laid-out width the framework shows the longest prefix + "…" that
// still fits in N lines. Same reasons as the one-line Ellipsize cut (TmpStyledText.Ellipsis.cs): TMP's own Ellipsis
// overflow mode can wipe a whole line on an exact fit, so overflowMode stays Overflow and the string is cut here.
// Reuses that partial's state (_full / _naturalW / _fitW / _padLe) and helpers (Prefix, EllipsisFor, WritePad).
//
// Line count = TMP's own wrap: set the probe string, ForceMeshUpdate(ignoreActiveState: true), read
// textInfo.lineCount. That is a full text layout per probe (TMP doesn't cache — WindowBuilder-Patterns.md), so it runs
// only when the string, the font size or the rect width changes (~log2 n probes), and never for a string whose
// UNWRAPPED width already fits the rect (one line — the common case for short names). The per-apply poll
// (FitToRect) reads rect.width alone. Alignment is TMP's per line, so centred text stays centred.
internal sealed partial class TmpStyledText
{
    private readonly int _maxLines;   // >0 = wrap + cut to this many lines (wins over _ellipsis)

    private void SetLineCapped(string s)
    {
        _full = s ?? "";
        MeasureAndCapLines();
    }

    private void MeasureAndCapLines()
    {
        _t.text = _full;
        // Unwrapped width of the FULL string → the owned preferred width (WritePad), so the shown cut never shrinks
        // its own slot (same contract as Ellipsize). The slot's width comes from the layout (Cell / fixed Width).
        _naturalW = _t.preferredWidth;
        WritePad();
        _fitW = -1f;
        CapLinesToRect();
    }

    private void CapLinesToRect()
    {
        var w = _t.rectTransform.rect.width;
        if (Mathf.Abs(w - _fitW) <= 0.5f) return;
        _fitW = w;
        _t.text = _full;
        // Not laid out yet (width ≤ 1) → the full string; the first real width re-fits it. One unwrapped line that
        // fits needs no line count at all.
        if (w <= 1f || _naturalW <= w) return;
        if (LineCount(_full) <= _maxLines) return;
        _t.text = CutToLines();
    }

    // Longest prefix whose prefix + ellipsis wraps into _maxLines lines (word wrap is monotonic enough in the prefix
    // length for a binary search). Nothing fits → just the ellipsis, or empty when even that needs too many lines.
    private string CutToLines()
    {
        var ell = EllipsisFor(_t.font);
        var best = LineCount(ell) <= _maxLines ? ell : "";
        int lo = 1, hi = _full.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var c = Prefix(mid) + ell;
            if (LineCount(c) <= _maxLines) { best = c; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }

    // Each probe is a real text set + one forced mesh generation (the same "set, then measure" rule as the one-line
    // Cut: GetTextInfo(string) would overwrite TMP's parse buffers without dirtying). ignoreActiveState: a row built
    // under an inactive parent still measures instead of returning a stale count.
    private int LineCount(string s)
    {
        _t.text = s;
        _t.ForceMeshUpdate(true);
        var info = _t.textInfo;
        return info == null ? 1 : info.lineCount;
    }
}
