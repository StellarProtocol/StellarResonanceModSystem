using UnityEngine;

namespace Stellar.Infrastructure.Game;

// Off-viewport cull half of the variable-height VirtualList (WindowBuilder.VariableList.cs, 2.26.1): which LATCHED
// rows inside the pool window are actually ACTIVE. The pool is ~viewport + a big buffer, and an active-but-clipped
// row still costs every frame (RectMask2D.PerformClipping walks every active maskable; every active CanvasRenderer is
// batched) — see the header's "Off-viewport cull" paragraph for the measurement. Split out for the 500-LoC gate; same
// nested VariableListBinding partial, so its fields/constants resolve unqualified.
//
// Invariants this keeps:
//   • Only LATCHED rows cull. An unlatched row must stay activeSelf to be measured (parked off-screen instead).
//   • A culled row keeps its slot (_slotLogical) and its latch, and Reposition still sizes + places it, so a reveal is
//     a bare SetActive(true) — no re-measure, no estimate pop, no offset churn.
//   • Reveal happens inside VirtualList Apply, which WindowToken.Apply runs BEFORE the Conds and value passes, so a
//     re-activated slot's Conds/texts/bubble (BubbleBinding re-measures on becoming active) are current the SAME tick.
//   • The anchor (topmost visible row) and every row the StickToBottom pin shows are in view → never culled.
//   • Diffed: SetActive only on an actual flip, and Cull runs only when the view (scroll Y, viewport height, moving
//     vs at rest) changed since the last pass — an idle list does zero cull work.
// Honest limit: applies run at ~10 Hz while ScrollRect moves the content every frame, so a row is revealed up to one
// apply late; the margin (≥ 2 rows / 160 px, + fling velocity × 0.25 s) covers wheel steps, drags and flings, but a
// scrollbar-thumb yank across hundreds of px can show a blank edge for one apply — the same lag the pool window
// already has at its TOP edge (`first` = the topmost visible row, so nothing above it is pooled until the next apply).
internal sealed partial class WindowBuilder
{
    internal sealed partial class VariableListBinding
    {
        private const float CullMarginMinPx = 160f;    // floor of the keep-active band above + below the viewport
        private const float CullLookaheadSec = 0.25f;  // + |fling velocity| × this: > one ~10 Hz apply of travel
        private float _viewY, _viewH;                  // this apply's FINAL scroll offset + viewport height (set in Apply)
        private float _cullMargin;                     // this pass's band, px (BeginCullPass)
        private float _cullKeyY = float.NaN, _cullKeyH = -1f;   // view the last pass ran for (_cullKeyH = -1 forces one)
        private bool _cullKeyMoving;

        // Snapshot this pass's margin and record the view it runs for. Returns true iff the view changed since the
        // last pass (Cull's early-out). Exact float compares are intended: an unmoved list re-reads the identical
        // anchoredPosition, and ScrollRect zeroes a decayed fling velocity, so a list at rest stops re-culling.
        private bool BeginCullPass()
        {
            var vel = Sr != null ? Mathf.Abs(Sr.velocity.y) : 0f;
            var moving = vel >= 1f;
            var due = _viewY != _cullKeyY || _viewH != _cullKeyH || moving != _cullKeyMoving;
            _cullKeyY = _viewY; _cullKeyH = _viewH; _cullKeyMoving = moving;
            _cullMargin = Mathf.Max(2f * Estimate(), CullMarginMinPx) + vel * CullLookaheadSec;
            return due;
        }

        // Does data row l's placed rect [_off[l], _off[l+1]) overlap the viewport grown by the margin? Before the
        // viewport has a height (first layout) nothing culls — never hide blind.
        private bool InView(int l)
            => _viewH <= 0f || (_off[l + 1] > _viewY - _cullMargin && _off[l] < _viewY + _viewH + _cullMargin);

        // The per-scroll pass when Reposition did NOT run (same `first`, count and latches — e.g. scrolling within a
        // row, or a viewport resize). Re-diffs every LATCHED shown slot against the current view. A slot whose row
        // lost its latch while culled (width-change InvalidateHeights, which triggers no Reposition) is inactive yet
        // must be active + parked to re-measure — that is exactly what Reposition does, so hand the window to it.
        private void Cull(int first, int count, int pool, bool bottomFirst)
        {
            if (!BeginCullPass()) return;
            for (var k = 0; k < Slots.Length; k++)
            {
                var l = _slotLogical[k]; var rt = Slots[k];
                if (l < 0 || l >= count || rt == null) continue;
                if (_measured[l]) { ShowLatched(l, rt, InView(l)); continue; }
                if (!rt.gameObject.activeSelf) { Reposition(first, count, pool, bottomFirst); return; }
            }
        }

        // Diffed SetActive for a shown LATCHED row (Reposition + Cull share it).
        //   • Cull: drop any in-flight provisional (a RowRevision re-measure mid-confirm) — the two-read confirm must
        //     come from consecutive applies while the row stayed shown (same rule as a slot leaving its index).
        //   • Reveal: arm the RowId / RowRevision checks NOW (ArmOnReveal) rather than on the next apply's detect.
        private void ShowLatched(int l, RectTransform rt, bool on)
        {
            var go = rt.gameObject;
            if (go.activeSelf == on) return;
            go.SetActive(on);
            if (on) ArmOnReveal(l);
            else { _prov[l] = false; _provH[l] = 0f; _tries[l] = 0; }
        }

        // A culled row is not measured (MeasureActiveSlots skips inactive slots), so a ring shift / revision bump that
        // happened while it was culled is only noticed once it is back. Detect-and-arm needs a one-apply deferral only
        // because, on the detect apply, the slot still SHOWS the old content (Measure runs before the value passes).
        // A row revealed here is rebound by THIS apply's value passes, so by the next apply its content is current —
        // arm now and the verify / in-place re-measure runs one apply earlier (the row was revealed at margin distance,
        // so it normally corrects before it scrolls into sight). Same state transitions as VerifyLatchedRow /
        // RemeasureRevisedRow's own detect branches (.Measure.cs).
        private void ArmOnReveal(int l)
        {
            if (RowId != null && !_verify[l]) { var id = RowId(l); if (id != _hId[l]) { _hId[l] = id; _verify[l] = true; } }
            if (RowRevision == null) return;
            var rev = RowRevision(l);
            if (rev != _hRev[l]) { _hRev[l] = rev; _remeasure[l] = true; _prov[l] = false; _tries[l] = 0; }
        }
    }
}
