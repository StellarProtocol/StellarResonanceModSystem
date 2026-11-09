using UnityEngine;

namespace Stellar.Infrastructure.Game;

// TextElement.MaxLines on the TMP path (framework 2.30.0; first user = the launcher's two-line plugin names): when the
// text is wider than its laid-out width, the framework breaks it into lines itself (WordWrapCap: only at spaces /
// after hyphens / between CJK characters; a word wider than the line is cut with "…" on its own line; the last of N
// lines ends in "…" when more text follows) and shows them joined with '\n'. TMP's own word wrap is OFF for MaxLines
// text (TmpStyledText.cs): TMP breaks an over-long word mid-word ("CombatMete" / "r", in-game 2026-11-09), and an
// exact-fit line could be re-wrapped by float noise. Same reasons as the one-line Ellipsize cut for doing the cut here
// (TmpStyledText.Ellipsis.cs): overflowMode stays Overflow. Reuses that partial's state (_full / _naturalW / _fitW /
// _padLe) and helpers (Measure, EllipsisFor, WritePad).
//
// Each probe is one unwrapped TMP width measure (a full text layout — TMP doesn't cache, WindowBuilder-Patterns.md), so
// this runs only when the string, the font size or the rect width changes, and never for a string whose UNWRAPPED
// width already fits the rect (one line — the common case for short names). The per-apply poll (FitToRect) reads
// rect.width alone. Alignment is TMP's per line, so centred text stays centred.
internal sealed partial class TmpStyledText
{
    private readonly int _maxLines;   // >0 = break into lines + cut to this many (wins over _ellipsis)

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
        // Not laid out yet (width ≤ 1) → the full string; the first real width re-fits it. One unwrapped line that
        // fits needs no line breaking at all.
        if (w <= 1f || _naturalW <= w) { _t.text = _full; return; }
        _t.text = WordWrapCap.Cap(_full, Measure, w, _maxLines, EllipsisFor(_t.font));
    }
}
