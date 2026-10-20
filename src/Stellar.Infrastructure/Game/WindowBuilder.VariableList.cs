using System;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Variable-height sibling of WindowBuilder.VirtualList.cs (2.25.0). The uniform list multiplies one RowHeight;
// this one renders rows that WRAP to different heights (chat bubbles) while still materializing only a fixed
// pool of ~viewport+buffer GameObjects. It keeps a per-data-index height model: a row's uGUI preferred height
// (the same mechanic BubbleElement/ContentSizeFitter use) is measured while it is bound to a slot, but it is only
// LATCHED into the cache once it has been STABLE across two consecutive measurements (within StableEps); a latched
// row is final and never re-measured. Un-measured rows use a running-average estimate so the scrollbar is roughly
// right immediately and sharpens as rows scroll through. Cumulative offsets (prefix sum over Count, bounded to a
// couple hundred) place the slots and derive the height. An un-measured row is NOT drawn at that estimate offset,
// though: the estimate is usually SHORTER than the row's real wrapped height, so packing rows at the estimate would
// make the real-sized slots OVERLAP until they latch (worst on a ResetScroll/tab switch, where EVERY row goes
// unmeasured at once and the viewport flashes overlapping bubbles for ~2-3 applies). So Reposition PARKS an
// unmeasured row off-screen (above the viewport top, clipped by the viewport's RectMask2D) while keeping it
// active + sized so it can still be measured; the apply it latches, it snaps on-screen at its true offset. Net: the
// viewport briefly blanks on a tab switch instead of flashing overlapped rows (see OffscreenY + Reposition).
//
// Why "measure until stable, then latch" and not either extreme (the two bugs this file has carried): re-reading
// GetPreferredHeight and committing it EVERY apply (the 2.25.0..2.25.2 design) let sub-pixel uGUI layout jitter —
// and the BubbleElement width-solve settling a tick behind — wobble a SETTLED on-screen row's height across the
// change threshold frame-to-frame; every wobble rebuilt the offsets and re-drove the scroll, so rows "shifted back
// and forth" while the user scrolled. Measuring exactly ONCE (2.25.3) killed that jitter but latched the FIRST
// reading — and BubbleElement solves a row's WIDTH a tick AFTER the first measurement, so that first reading is
// taken while the bubble is still ~0 wide: the text wraps to ~1 char per line → a hugely TALL preferred height that
// got locked in permanently, leaving a large empty gap below every message even after the text visually corrected.
// Stable-confirm (2.25.4) waits out that width-solve lag without depending on its exact timing: a pre-width-solve
// tall reading never matches the next (settled) reading within StableEps, so it stays provisional and never latches;
// a genuinely settled row matches its own next reading and latches on the 2nd measure, after which it is never
// re-measured — so the 2.25.3 no-jitter property is fully preserved. A latched height is dropped + re-measured ONLY
// on: a viewport-WIDTH change (rows re-wrap → every height changes), ResetScroll (tab switch), or a Count DECREASE
// (rows dropped/filtered/front-dropped → data indices remap). An append (Count INCREASE) keeps the cache and
// measures only the new tail indices (they are simply unmeasured).
//
// Scroll stability: the only height that now changes under a steady view is a row's one-time estimate→measured
// correction as it scrolls into view. To absorb it without a jump we anchor to the topmost visible row — recompute
// the offsets, then set the scroll so that row's TOP stays under the viewport top (rows BELOW it reflow, the
// unavoidable minimum). Because the anchor row is itself cached-stable, every row from the anchor DOWN (the whole
// visible set bar the partial row entering at the very top) holds still. Honest limits: (1) the entering row pops
// from estimate to real height at the top/bottom edge; (2) a first-measure of a row ABOVE the viewport during an
// active flick can nudge the position a few px (we never zero the user's scroll velocity mid-flick); (3) a rolling
// buffer that front-drops WITHOUT changing Count remaps indices undetected — cached heights are then one-row stale
// until the next width/shrink/reset invalidation (bounded, self-heals; no per-frame jitter in exchange).
//
// Split into its own partial for the 500-LoC file gate; same WindowBuilder partial class, so the nested binding
// types resolve unqualified.
internal sealed partial class WindowBuilder
{
    // Both the uniform VirtualListBinding and this one live behind one list on WindowToken (applied FIRST each
    // tick, before Conds/values) so a slot's Funcs resolve the freshly-windowed data index.
    internal interface IVirtualListBinding
    {
        bool Apply();
    }

    // Build path for VirtualListElement { VariableHeight = true }. Mirrors the uniform BuildVirtualList pool
    // construction (absolutely-positioned slots as children of a non-layout content rect) but seeds each slot at
    // the baseline estimate and captures the viewport (needed for the stick-to-bottom / at-bottom math).
    private void BuildVariableList(VirtualListElement v, Transform parent, WindowToken token)
    {
        var (viewport, content, scrollRect) = BuildScrollViewport(parent, v.Height, token);
        content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f); content.anchoredPosition = Vector2.zero;

        var baseline = v.RowHeight > 0f ? v.RowHeight : 28f;   // RowHeight doubles as the un-measured estimate seed
        var slots = new RectTransform[v.Pool.Count];
        for (var i = 0; i < v.Pool.Count; i++)
        {
            var slot = UGuiPrimitives.NewChild("VSlot", content);
            var srt = slot.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0f, 1f); srt.anchorMax = new Vector2(1f, 1f);
            srt.pivot = new Vector2(0.5f, 1f);
            srt.sizeDelta = new Vector2(0f, baseline);
            // VLG (childControlHeight, NOT force-expand) → the row keeps its preferred height = what we measure;
            // ExpandColumnWidth stretches it to the slot width so a wrapping child wraps at the real width.
            UGuiPrimitives.AddLayout(slot, gap: 0f, columns: UGuiPrimitives.ColumnMode); ExpandColumnWidth(slot);
            BuildElement(v.Pool[i], slot.transform, token);
            slots[i] = srt;
        }

        token.VirtualLists.Add(new VariableListBinding
        {
            Sr = scrollRect, Content = content, Viewport = viewport, Slots = slots,
            Count = v.Count, OnWindow = v.OnWindow, ResetScroll = v.ResetScroll,
            Baseline = baseline, StickToBottom = v.StickToBottom,
        });
    }

    // Poll-diffed measured-height virtual list. Each Apply: measures what the slots currently show (self-healing),
    // rebuilds the cumulative offsets when the model changed, chooses a stable scroll position, then repositions +
    // activates the pooled slots over the visible window. Returns true only when Count changed (the content extent
    // moved) — offset-driven repositioning is absolute and needs no window-level layout rebuild.
    internal sealed class VariableListBinding : IVirtualListBinding
    {
        public ScrollRect Sr = null!;
        public RectTransform Content = null!;
        public RectTransform Viewport = null!;
        public RectTransform[] Slots = Array.Empty<RectTransform>();
        public Func<int> Count = null!;
        public Action<int> OnWindow = null!;
        public Func<bool>? ResetScroll;
        public float Baseline = 28f;
        public bool StickToBottom;

        private float[] _h = Array.Empty<float>();        // measured heights, keyed by data index
        private bool[] _measured = Array.Empty<bool>();    // has index i been LATCHED (final — never re-measured)?
        private float[] _provH = Array.Empty<float>();     // last apply's provisional (unconfirmed) measured height, by index
        private bool[] _prov = Array.Empty<bool>();        // a provisional measurement exists for index i, awaiting a 2nd-reading confirm
        private float[] _off = Array.Empty<float>();       // cumulative top offsets, length ≥ count+1
        private int[] _slotLogical = Array.Empty<int>();   // data index each pool slot currently shows (-1 = hidden)
        private float _sum; private int _n;                // running sum/count of measured heights → average estimate
        private float _lastViewportW = -1f;                // last positive viewport width; a change re-wraps rows → drop the height cache
        private int _lastFirst = -1, _lastCount = -1;
        private bool _firstApply = true, _offDirty = true;
        private const float BottomEps = 2f;   // within this many px of the bottom counts as "at the bottom"
        // Park an unmeasured row here (a large POSITIVE y) instead of at its estimate offset: content and slots are
        // top-anchored with pivot y=1, so on-screen rows sit at NEGATIVE y going DOWN — a big positive y puts the row
        // ABOVE the viewport top, where the ScrollRect viewport's RectMask2D clips it away. This hides a not-yet-
        // latched row until its height is measured, killing the tab-switch/estimate OVERLAP flash (see Reposition).
        private const float OffscreenY = 1_000_000f;
        // A height latches only after two consecutive measurements agree within this many px. Must exceed sub-pixel
        // uGUI wobble (~0.5px) so a settled row confirms on its 2nd measure, yet stay far below the gap between a
        // pre-width-solve (tall, ~1 char/line) reading and the real height so such a reading never falsely confirms.
        private const float StableEps = 1.5f;

        public bool Apply()
        {
            if (Content == null || !Content.gameObject.activeInHierarchy) return false;
            EnsureSlotMap();
            var count = Count(); if (count < 0) count = 0;
            var pool = Slots.Length;

            var reset = ResetScroll?.Invoke() == true;
            if (reset) ResetState();

            var viewportH = Viewport != null ? Viewport.rect.height : 0f;
            var scrollY = Mathf.Max(0f, Content.anchoredPosition.y);
            // Captured from the OLD model (before any rebuild) for anchoring + the at-bottom decision.
            var anchorIdx = _lastFirst > 0 ? _lastFirst : 0;
            var anchorTopOld = (_lastCount >= 0 && anchorIdx < _off.Length) ? _off[anchorIdx] : 0f;
            var oldTotal = (_lastCount >= 0 && _lastCount < _off.Length) ? _off[_lastCount] : 0f;
            var wasAtBottom = StickToBottom && (_firstApply || _lastCount < 0
                || scrollY >= VariableListMath.MaxScroll(oldTotal, viewportH) - BottomEps);

            EnsureCapacity(count);
            // Heights are a per-data-index CACHE (see file header). Drop + re-measure only when a stored height is
            // stale: the viewport WIDTH changed (wrap widths change → every height changes), or Count DECREASED
            // (rows dropped/filtered/front-dropped → indices remap). ResetScroll handled its own clear above; an
            // append (Count INCREASE) keeps the cache and leaves the new tail indices unmeasured. Invalidate BEFORE
            // measuring so the now-unmeasured active slots re-measure THIS apply and only off-screen rows fall back
            // to the estimate — the anchor compensation below then keeps the view from jumping.
            var viewportW = Viewport != null ? Viewport.rect.width : 0f;
            if (viewportW > 0f && _lastViewportW > 0f && !Mathf.Approximately(viewportW, _lastViewportW)) InvalidateHeights();
            if (viewportW > 0f) _lastViewportW = viewportW;
            if (count < _lastCount) InvalidateHeights();
            var measuredChanged = MeasureActiveSlots(count);
            var countChanged = count != _lastCount;
            if (_offDirty || countChanged || measuredChanged || reset)
            { VariableListMath.BuildOffsets(count, _off, _h, _measured, Estimate()); _offDirty = false; }

            var total = _off[count];
            var maxScroll = VariableListMath.MaxScroll(total, viewportH);
            if (!Mathf.Approximately(Content.sizeDelta.y, total)) Content.sizeDelta = new Vector2(0f, total);

            if (reset || _firstApply) scrollY = StickToBottom ? maxScroll : 0f;
            else if (wasAtBottom) scrollY = maxScroll;   // glued to the TRUE bottom: covers append AND estimate-refine
            else if (measuredChanged && anchorIdx <= count) scrollY = Mathf.Clamp(_off[anchorIdx] + (scrollY - anchorTopOld), 0f, maxScroll);
            else scrollY = Mathf.Min(scrollY, maxScroll);
            if (!Mathf.Approximately(scrollY, Content.anchoredPosition.y)) SetScroll(scrollY, reset || _firstApply);

            var first = VariableListMath.FirstIndex(scrollY, _off, count, pool);
            if (first != _lastFirst || countChanged || measuredChanged || reset) Reposition(first, count, pool);

            _lastFirst = first; _lastCount = count; _firstApply = false;
            return countChanged;
        }

        // The running estimate = average of measured rows (Baseline until any row is measured). Used for every
        // not-yet-shown row, so the total/scrollbar is approximate immediately and tightens as rows are measured.
        private float Estimate() => _n > 0 ? _sum / _n : Baseline;

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
        private bool MeasureActiveSlots(int count)
        {
            var changed = false;
            for (var k = 0; k < Slots.Length; k++)
            {
                var l = _slotLogical[k];
                if (l < 0 || l >= count || _measured[l]) continue;   // latched — never re-measure a settled row
                var rt = Slots[k];
                if (rt == null || !rt.gameObject.activeSelf) continue;
                try { LayoutRebuilder.ForceRebuildLayoutImmediate(rt); } catch { }
                var h = LayoutUtility.GetPreferredHeight(rt);
                if (h < 1f) continue;   // not laid out yet — leave for next apply
                if (_prov[l] && Mathf.Abs(h - _provH[l]) <= StableEps)   // stable across two reads → commit
                { _h[l] = h; _measured[l] = true; _sum += h; _n++; changed = true; _prov[l] = false; }
                else { _provH[l] = h; _prov[l] = true; }   // first/unstable reading — provisional only, no offset churn
            }
            if (changed) _offDirty = true;
            return changed;
        }

        // Window the pool: tell the consumer the first data index (so slot Funcs resolve first+k this poll), then
        // place each slot at its cumulative offset and size it to its row height; slots past the end hide.
        // A row whose height is NOT yet latched (!_measured[logical]) is parked OFF-SCREEN (at OffscreenY, where the
        // viewport's RectMask2D clips it) rather than drawn at its estimate offset: the estimate is a guess (usually
        // SHORTER than a real wrapping bubble), so the offsets pack such rows too tight and the real-sized slots
        // would visibly OVERLAP for the 2-3 applies it takes MeasureActiveSlots to latch them — most visible on a
        // ResetScroll/tab switch, where every row goes unmeasured at once. We keep the slot ACTIVE and still size it
        // to the estimate so uGUI lays it out and MeasureActiveSlots can read its preferred height (measurement needs
        // activeSelf — so we hide by POSITION, never SetActive(false)). The apply it latches, measuredChanged re-runs
        // Reposition and it snaps to its true on-screen offset. Net: the viewport briefly blanks (~2 frames) on a tab
        // switch instead of flashing overlapped rows; already-measured rows (steady state + scroll) always take the
        // on-screen branch, so nothing there changes.
        private void Reposition(int first, int count, int pool)
        {
            OnWindow(first);
            for (var k = 0; k < pool; k++)
            {
                var rt = Slots[k];
                if (rt == null) continue;
                var logical = first + k;
                var show = logical < count;
                if (rt.gameObject.activeSelf != show) rt.gameObject.SetActive(show);
                if (show)
                {
                    // Size to the row height in BOTH cases (real height for a latched row, estimate for an unmeasured
                    // one) so the VLG lays the row out and it can be measured next apply.
                    var rowH = _off[logical + 1] - _off[logical];
                    if (rowH > 0f) rt.sizeDelta = new Vector2(0f, rowH);
                    // Latched → draw at its true offset. Unmeasured → park off-screen (clipped) until it latches, so
                    // it never paints at the wrong estimate position (the tab-switch overlap flash).
                    rt.anchoredPosition = _measured[logical]
                        ? new Vector2(0f, -_off[logical])
                        : new Vector2(0f, OffscreenY);
                    _slotLogical[k] = logical;
                }
                else _slotLogical[k] = -1;
            }
        }

        private void SetScroll(float y, bool zeroVelocity)
        {
            Content.anchoredPosition = new Vector2(Content.anchoredPosition.x, y);
            if (zeroVelocity && Sr != null) Sr.velocity = Vector2.zero;   // only on a reset/first pin, never mid-flick
        }

        private void EnsureSlotMap()
        {
            if (_slotLogical.Length == Slots.Length) return;
            _slotLogical = new int[Slots.Length];
            for (var k = 0; k < _slotLogical.Length; k++) _slotLogical[k] = -1;
        }

        // Grow the index-keyed arrays (only ever grows; Count climbs to the rolling-buffer cap then holds).
        private void EnsureCapacity(int count)
        {
            if (count + 1 <= _off.Length && count <= _h.Length) return;
            var cap = Mathf.Max(count, _h.Length == 0 ? 8 : _h.Length * 2);
            var h = new float[cap]; var m = new bool[cap];
            var ph = new float[cap]; var p = new bool[cap];
            Array.Copy(_h, h, _h.Length); Array.Copy(_measured, m, _measured.Length);
            Array.Copy(_provH, ph, _provH.Length); Array.Copy(_prov, p, _prov.Length);
            _h = h; _measured = m; _provH = ph; _prov = p; _off = new float[cap + 1];
            _offDirty = true;
        }

        // Drop every cached height but KEEP the scroll position and slot mapping. Used when a change makes stored
        // heights stale yet the view should stay put: a viewport-WIDTH change (rows re-wrap) or a Count DECREASE
        // (shrink / filter / rolling-buffer front-drop → data indices now point at different rows). Active slots
        // re-measure the SAME apply (they are unmeasured again); off-screen rows fall back to the estimate until
        // shown; the topmost-visible anchor compensation keeps the view from jumping. Softer than ResetState,
        // which also re-pins the scroll and forgets the window.
        private void InvalidateHeights()
        {
            Array.Clear(_measured, 0, _measured.Length);
            Array.Clear(_prov, 0, _prov.Length);   // rows re-run the stable-confirm from scratch at the new width
            _sum = 0f; _n = 0; _offDirty = true;
        }

        // A reset (filter/tab switch via ResetScroll) forgets the model: data indices now map to different rows,
        // so stored heights are stale. Clearing re-measures from the next shown frame; the scroll re-pins.
        private void ResetState()
        {
            Array.Clear(_measured, 0, _measured.Length);
            Array.Clear(_prov, 0, _prov.Length);
            for (var k = 0; k < _slotLogical.Length; k++) _slotLogical[k] = -1;
            _sum = 0f; _n = 0; _offDirty = true; _lastViewportW = -1f;
            _lastFirst = -1; _lastCount = -1; _firstApply = true;
        }
    }
}
