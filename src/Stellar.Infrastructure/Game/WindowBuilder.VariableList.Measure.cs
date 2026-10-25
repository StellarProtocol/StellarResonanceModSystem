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
        // First-build cap: at most this many NOT-yet-latched rows are active (bound + measured) at once. Binding a row
        // to content it has never shown is the expensive part of a window apply — the slot's lazy Cond branch build,
        // TMP glyph rasterization during the forced layout, the avatar poll, the bubble width-solve — and a login
        // history batch or a tab switch used to do ~40 of them in ONE apply (a 0.9-4.3 s freeze). Each admitted row
        // latches in ~2 applies (the stable-confirm) and frees its budget, so a full pool fills in over a few applies.
        // Fixed constant (no adaptive tuning): 8 ≈ one viewport of chat rows, so the VISIBLE window still fills in
        // the first round.
        private const int MaxUnlatchedRows = 8;
        private bool[] _admit = System.Array.Empty<bool>();   // per SLOT: shown this Reposition (PlanAdmission's verdict)
        // per DATA INDEX: this index has latched at least once since the last ResetState. A width/count
        // InvalidateHeights drops the latch but the content under the index is the same (re-wrap) or a one-row ring
        // neighbour that has already been laid out, so its re-measure is cheap — exempt from the cap so a resize or a
        // ring front-drop re-latches the whole window as fast as before. ResetState (new tab) clears it.
        private bool[] _seen = System.Array.Empty<bool>();
        // per DATA INDEX: failed stable-confirms of the current measuring run (GiveUpConfirm caps it; reset on latch/leave).
        private byte[] _tries = System.Array.Empty<byte>();

        // Decide, per pool slot, whether its row is shown this Reposition. Latched rows always are (they are on-screen
        // at their true offset — hiding one would open a visible hole). Unlatched rows are admitted under the budget:
        //   1. rows ALREADY in flight (still active in the same slot on the same index) keep their admission, so a row
        //      mid-confirm is never yanked before it latches (each one only spends budget it already holds);
        //   2. rows exempt via _seen (cheap re-measure, see above) are admitted free;
        //   3. the remaining budget goes to the rows nearest the anchor — from the BOTTOM up while pinned (the newest
        //      rows the user is looking at, and the pin keeps following as each latches and the content grows), else
        //      from the top (the topmost-visible row is the scroll anchor).
        // A row held back is simply not shown; every latch sets measuredChanged → Reposition → this re-plans, so the
        // backlog drains without a timer. A row that NEVER latches (unmeasurable, h < 1) keeps its budget slot, exactly
        // as it stays parked forever today; only 8 such rows inside one window could starve the rest.
        // Both loops walk the WINDOW (d = row offset from first, so "bottom first" stays bottom-first in data order) and
        // address the slot showing that row through SlotOf — the identity d in the default mode, l % pool in StableSlots
        // mode (a bijection over the window either way, so every slot's verdict is written exactly once).
        private void PlanAdmission(int first, int count, int pool, bool bottomFirst)
        {
            if (_admit.Length != pool) _admit = new bool[pool];
            var budget = MaxUnlatchedRows;
            for (var d = 0; d < pool; d++)
            {
                var k = SlotOf(first, d, pool); var l = first + d;
                _admit[k] = false;
                if (l < 0 || l >= count) continue;
                if (_measured[l] || _seen[l]) { _admit[k] = true; continue; }
                var rt = Slots[k];
                if (rt != null && rt.gameObject.activeSelf && _slotLogical[k] == l) { _admit[k] = true; budget--; }
            }
            for (var i = 0; i < pool && budget > 0; i++)
            {
                var d = bottomFirst ? pool - 1 - i : i;
                var k = SlotOf(first, d, pool); var l = first + d;
                if (l < 0 || l >= count || _admit[k]) continue;
                _admit[k] = true; budget--;
            }
        }

        // The pool slot that shows window row d (data index first + d). Default: slot d — so a one-row shift of `first`
        // rebinds EVERY slot. StableSlots (VirtualListElement): slot (first + d) % pool — a row keeps its slot while it
        // stays in the window, so an append/scroll step recycles only the slot whose row left; the consumer resolves a
        // slot with VariableListMath.StableSlotRow, the inverse of this.
        private int SlotOf(int first, int d, int pool) => StableSlots && pool > 0 ? (first + d) % pool : d;

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
            var changed = false;
            for (var k = 0; k < Slots.Length; k++)
            {
                var l = _slotLogical[k];
                if (l < 0 || l >= count) continue;
                var rt = Slots[k];
                if (rt == null || !rt.gameObject.activeSelf) continue;

                // A latched row is final — EXCEPT in RowId mode, where VerifyLatchedRow re-checks its identity and may
                // drop the latch (→ changed, so the offsets/scroll recompute). RowId==null keeps it byte-identical.
                // A latch that survives may still be re-measured IN PLACE when its RowRevision moved (RemeasureRevisedRow).
                if (_measured[l]) { if (VerifyLatchedRow(l, rt) || RemeasureRevisedRow(l, rt)) changed = true; continue; }

                try { LayoutRebuilder.ForceRebuildLayoutImmediate(rt); } catch { }
                UiPerfStats.Count(UiCounter.RowMeasure);   // [Hitch] ui:rowMeasure — 0 at rest once every shown row latched
                var h = LayoutUtility.GetPreferredHeight(rt);
                if (h < 1f) continue;   // not laid out yet — leave for next apply
                if (_prov[l] && Mathf.Abs(h - _provH[l]) <= StableEps) { Latch(l, h); changed = true; }   // stable across two reads → commit
                else if (GiveUpConfirm(l)) { Latch(l, Mathf.Max(h, _provH[l])); changed = true; }
                else { _provH[l] = h; _prov[l] = true; }   // first/unstable reading — provisional only, no offset churn
            }
            if (changed) _offDirty = true;
            return changed;
        }

        // A row whose consecutive readings never agree within StableEps (sub-pixel layout wobble that straddles the
        // threshold, a bubble whose wrap flips between two states) used to stay unlatched FOREVER — a forced slot rebuild
        // on every apply, for as long as it was shown. After MaxFailedConfirms failed confirms (~0.4 s) give up waiting
        // and latch the LARGER of the last two readings: a too-tall row leaves a small gap, a too-short one would clip.
        // The first reading never counts (nothing to compare to), so the normal pre-width-solve tall reading → settled
        // reading → confirm sequence never comes near the cap.
        private const int MaxFailedConfirms = 3;
        private bool GiveUpConfirm(int l)
        {
            if (!_prov[l] || ++_tries[l] < MaxFailedConfirms) return false;
            UiPerfStats.Count(UiCounter.RowForceLatch);
            return true;
        }

        // Commit index l's height (a confirmed or a given-up reading) as its final latched height.
        private void Latch(int l, float h)
        {
            _h[l] = h; _measured[l] = true; _seen[l] = true; _sum += h; _n++; _prov[l] = false; _tries[l] = 0;
            if (RowId != null) _hId[l] = RowId(l);
            _remeasure[l] = false; if (RowRevision != null) _hRev[l] = RowRevision(l);   // latched at the CURRENT revision
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

        // RowRevision mode (VirtualListElement.RowRevision): the consumer says a LATCHED row's content changed (a chat
        // bubble swapped to its translation, gained a status line …). Unlike VerifyLatchedRow we do NOT drop the latch —
        // an unlatched row is parked off-screen (Reposition), which would blink the row out and jump the rows below for
        // the 2-3 applies a re-latch takes. Instead the row keeps its old height on screen while it is re-measured with
        // the same two-read stable-confirm as a first latch (_prov/_provH are free while a row is latched), and only the
        // confirmed new height replaces _h — one offset rebuild, anchored/pinned by Apply like any other latch.
        //   • revision moved → record it + arm, but don't measure THIS apply (the slot's content refresh runs after
        //     MeasureActiveSlots — same reason as the deferred identity verify).
        //   • armed → read; first/unstable reading is provisional; two agreeing reads commit (if the height really moved).
        // Returns true iff the committed height changed (caller marks the offsets dirty). RowRevision==null → no-op.
        private bool RemeasureRevisedRow(int l, RectTransform rt)
        {
            if (RowRevision == null) return false;
            var rev = RowRevision(l);
            if (rev != _hRev[l]) { _hRev[l] = rev; _remeasure[l] = true; _prov[l] = false; _tries[l] = 0; return false; }
            if (!_remeasure[l]) return false;
            try { LayoutRebuilder.ForceRebuildLayoutImmediate(rt); } catch { }
            UiPerfStats.Count(UiCounter.RowMeasure);
            var h = LayoutUtility.GetPreferredHeight(rt);
            if (h < 1f) return false;   // not laid out yet — stay armed
            // Same try cap as a first latch (GiveUpConfirm): a revised row that never settles commits its larger reading.
            if (_prov[l] && Mathf.Abs(h - _provH[l]) > StableEps && GiveUpConfirm(l)) h = Mathf.Max(h, _provH[l]);
            else if (!_prov[l] || Mathf.Abs(h - _provH[l]) > StableEps) { _provH[l] = h; _prov[l] = true; return false; }
            _remeasure[l] = false; _prov[l] = false; _tries[l] = 0;
            if (Mathf.Abs(h - _h[l]) <= StableEps) return false;   // same height after all → nothing to re-flow
            _sum += h - _h[l]; _h[l] = h;
            return true;
        }
    }
}
