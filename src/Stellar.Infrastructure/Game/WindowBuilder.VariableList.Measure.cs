using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Height measurement + latch half of the variable-height VirtualList (WindowBuilder.VariableList.cs): the per-apply
// measure of active, not-yet-latched slots and the RowId identity re-verify of latched ones. Split out of the main
// file for the 500-LoC gate; same nested VariableListBinding partial, so its fields/constants resolve unqualified.
internal sealed partial class WindowBuilder
{
    internal sealed partial class VariableListBinding
    {
        // Measure each active, not-yet-latched slot and LATCH its height only once two consecutive readings agree.
        // Each apply, for a row still awaiting its final height: force one immediate layout rebuild, read the uGUI
        // preferred height h, and compare to last apply's provisional reading. If they agree within StableEps the
        // row has SETTLED → latch h into the cache (keyed by DATA INDEX) and never measure it again; otherwise just
        // record h as the new provisional and wait. This waits out BubbleBinding's width-solve lag — the first
        // reading is taken while the bubble is still ~0 wide (text wraps to ~1 char/line → a far-too-TALL height),
        // which never matches the next (settled) reading, so it stays provisional and never locks in a tall gap.
        // A latched row is skipped forever — re-reading a settled row every apply was the 2.25.0..2.25.2 jitter —
        // so the no-jitter property holds. While a row is only provisional it keeps using the running Estimate() in
        // the offsets (we do NOT set changed), so the settle window adds no offset churn. Latch invalidation (width
        // change / Count decrease / reset) is the only other way a latched height changes. Returns true iff a NEW
        // row LATCHED this apply (an estimate became real → the offsets and scroll need the one-time anchor comp).
        //
        // RowId mode (optional): a latched height is keyed by DATA INDEX, but a front-dropping ring (header limit #3)
        // re-maps an index to a DIFFERENT message at constant Count, so the latch silently goes stale and a rigid
        // tall row (a sticker) inherits a short row's height → permanent overflow. When RowId is supplied we stamp,
        // on latch, the id the height was measured FOR (_hId[l]); each apply we compare it to the CURRENT id and, when
        // it changed, re-verify the height ONCE and drop the latch only if grossly wrong (> VerifyEps). The verify is
        // DEFERRED one apply: MeasureActiveSlots runs before this apply's Conds/ApplyValues, so on the apply the id
        // flips the slot still shows the OLD message — measuring then would read stale content and false-negative. We
        // record the new id + arm _verify[l], and measure on the NEXT apply once the displayed content is current. A
        // row whose id is unchanged is still skipped with zero re-measure, so steady scroll stays cheap (RowId==null
        // keeps the original behaviour byte-for-byte).
        private bool MeasureActiveSlots(int count)
        {
            var hT = Stellar.Abstractions.Diagnostics.HitchProbe.Begin();   // diag: [Hitch] vl:measure (incl. VerifyLatchedRow)
            var changed = false;
            for (var k = 0; k < Slots.Length; k++)
            {
                var l = _slotLogical[k];
                if (l < 0 || l >= count) continue;
                var rt = Slots[k];
                if (rt == null || !rt.gameObject.activeSelf) continue;

                // A latched row is final — EXCEPT in RowId mode, where VerifyLatchedRow re-checks its identity and may
                // drop the latch (→ changed, so the offsets/scroll recompute). RowId==null keeps it byte-identical.
                if (_measured[l]) { if (VerifyLatchedRow(l, rt)) changed = true; continue; }

                Stellar.Abstractions.Diagnostics.HitchProbe.Count("vl:measuredSlots", 1);
                try { LayoutRebuilder.ForceRebuildLayoutImmediate(rt); } catch { }
                var h = LayoutUtility.GetPreferredHeight(rt);
                if (h < 1f) continue;   // not laid out yet — leave for next apply
                if (_prov[l] && Mathf.Abs(h - _provH[l]) <= StableEps)   // stable across two reads → commit
                { _h[l] = h; _measured[l] = true; _sum += h; _n++; changed = true; _prov[l] = false; if (RowId != null) _hId[l] = RowId(l); }
                else { _provH[l] = h; _prov[l] = true; }   // first/unstable reading — provisional only, no offset churn
            }
            if (changed) _offDirty = true;
            Stellar.Abstractions.Diagnostics.HitchProbe.End("vl:measure", hT);
            return changed;
        }

        // Identity re-check for a LATCHED row in RowId mode (extracted from MeasureActiveSlots for the method-length
        // gate; semantics are unchanged). Returns true iff the latch was DROPPED (caller sets changed → the offsets
        // and scroll recompute, and the row re-latches via the normal 2-read confirm over the next applies, which
        // run AFTER content updates so they measure the CURRENT message).
        //   • RowId == null → the original behaviour: a settled row is final, never re-measured (byte-for-byte).
        //   • id unchanged  → cached height still belongs to the same message → keep the latch, no re-measure.
        //   • id changed    → can't trust a measure THIS apply (MeasureActiveSlots runs before the Conds/value passes
        //     that refresh the slot's content, so it still shows the previous message). Record the new id and arm a
        //     one-apply-deferred verify; keep the latch meanwhile (≤1 apply at the old height vs the PERMANENT stale
        //     latch it replaces). Next apply the content is current: re-measure once and drop the latch only if the
        //     height is grossly wrong (> VerifyEps ≫ StableEps → a same-height replacement keeps its latch, no
        //     re-flow). Never re-commit the verify reading directly — a measure right after a content swap can read a
        //     pre-width-solve too-tall value — so un-latch and let the 2-read confirm re-latch the truth.
        private bool VerifyLatchedRow(int l, RectTransform rt)
        {
            if (RowId == null) return false;
            var cur = RowId(l);
            if (!_verify[l])
            {
                if (cur == _hId[l]) return false;          // same message as cached → latch valid
                _hId[l] = cur; _verify[l] = true; return false;   // identity changed → arm the deferred verify
            }
            _verify[l] = false;
            if (cur != _hId[l]) { _hId[l] = cur; _verify[l] = true; return false; }   // shifted AGAIN mid-verify → re-arm
            try { LayoutRebuilder.ForceRebuildLayoutImmediate(rt); } catch { }
            var hv = LayoutUtility.GetPreferredHeight(rt);
            if (hv < 1f) { _verify[l] = true; return false; }   // not laid out yet → re-try next apply
            if (Mathf.Abs(hv - _h[l]) > VerifyEps)
            { _sum -= _h[l]; _n = Mathf.Max(0, _n - 1); _measured[l] = false; _prov[l] = false; _provH[l] = 0f; return true; }
            return false;
        }
    }
}
