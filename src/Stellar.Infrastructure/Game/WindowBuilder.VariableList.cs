using System;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Variable-height sibling of WindowBuilder.VirtualList.cs (2.25.0). The uniform list multiplies one RowHeight;
// this one renders rows that WRAP to different heights (chat bubbles) while still materializing only a fixed
// pool of ~viewport+buffer GameObjects. It keeps a per-data-index height model: a row is MEASURED (uGUI
// preferred height, the same mechanic BubbleElement/ContentSizeFitter use) the apply AFTER it is bound to a
// slot and had a layout pass; un-measured rows use a running-average estimate so the scrollbar is roughly right
// immediately and sharpens as rows scroll through. Cumulative offsets (prefix sum over Count, which is bounded
// to a couple hundred) place the slots and derive the content height.
//
// Scroll stability: when a measurement replaces an estimate, the content below that row shifts. To avoid a
// visible jump we anchor to the current topmost visible row — recompute the offsets, then set the scroll so that
// row's TOP stays under the viewport top (the rows BELOW it reflow, which is the unavoidable minimum). Honest
// limits: (1) if the just-measured row IS the anchor, its own content below the top edge still reflows; (2) a
// re-measure of a row ABOVE the viewport during an active flick can nudge the position a few px (we never zero
// the user's scroll velocity mid-flick); (3) the model keys on DATA INDEX, so a rolling buffer that drops its
// oldest row shifts every association — heights self-heal because active slots are re-measured every apply.
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
        private bool[] _measured = Array.Empty<bool>();    // has index i been measured yet?
        private float[] _off = Array.Empty<float>();       // cumulative top offsets, length ≥ count+1
        private int[] _slotLogical = Array.Empty<int>();   // data index each pool slot currently shows (-1 = hidden)
        private float _sum; private int _n;                // running sum/count of measured heights → average estimate
        private int _lastFirst = -1, _lastCount = -1;
        private bool _firstApply = true, _offDirty = true;
        private const float BottomEps = 2f;   // within this many px of the bottom counts as "at the bottom"

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

        // Measure every active slot against the index it currently shows. Unmeasured rows get a one-off immediate
        // layout rebuild (BubbleBinding already pinned their width LAST apply); measured rows just re-read the
        // cached preferred height, so a changed bubble (recycled index, reskin, font-scale) self-corrects cheaply.
        private bool MeasureActiveSlots(int count)
        {
            var changed = false;
            for (var k = 0; k < Slots.Length; k++)
            {
                var l = _slotLogical[k];
                if (l < 0 || l >= count) continue;
                var rt = Slots[k];
                if (rt == null || !rt.gameObject.activeSelf) continue;
                if (!_measured[l]) { try { LayoutRebuilder.ForceRebuildLayoutImmediate(rt); } catch { } }
                var h = LayoutUtility.GetPreferredHeight(rt);
                if (h < 1f) continue;   // not laid out yet — leave unmeasured, retry next apply
                if (!_measured[l]) { _h[l] = h; _measured[l] = true; _sum += h; _n++; changed = true; }
                else if (Mathf.Abs(h - _h[l]) > 0.5f) { _sum += h - _h[l]; _h[l] = h; changed = true; }
            }
            if (changed) _offDirty = true;
            return changed;
        }

        // Window the pool: tell the consumer the first data index (so slot Funcs resolve first+k this poll), then
        // place each slot at its cumulative offset and size it to its row height; slots past the end hide.
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
                    rt.anchoredPosition = new Vector2(0f, -_off[logical]);
                    var rowH = _off[logical + 1] - _off[logical];
                    if (rowH > 0f) rt.sizeDelta = new Vector2(0f, rowH);
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
            Array.Copy(_h, h, _h.Length); Array.Copy(_measured, m, _measured.Length);
            _h = h; _measured = m; _off = new float[cap + 1];
            _offDirty = true;
        }

        // A reset (filter/tab switch via ResetScroll) forgets the model: data indices now map to different rows,
        // so stored heights are stale. Clearing re-measures from the next shown frame; the scroll re-pins.
        private void ResetState()
        {
            Array.Clear(_measured, 0, _measured.Length);
            for (var k = 0; k < _slotLogical.Length; k++) _slotLogical[k] = -1;
            _sum = 0f; _n = 0; _offDirty = true;
            _lastFirst = -1; _lastCount = -1; _firstApply = true;
        }
    }
}
