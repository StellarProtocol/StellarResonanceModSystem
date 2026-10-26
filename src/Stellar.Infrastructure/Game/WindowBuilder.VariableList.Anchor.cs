using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

// Identity scroll anchor of the variable-height VirtualList (WindowBuilder.VariableList.cs), RowId mode only.
//
// Why: the scroll position is a PIXEL offset, and the anchor compensation in Apply keeps a row in place by its DATA
// INDEX. Both silently assume index i still shows the same message. A consumer that trims or rebuilds its list breaks
// that — ChatTools trims its filtered log in chunks (Count drops by ~190 rows at once) and fully rebuilds it from the
// front-dropping 500-message ring on history batches / out-of-order timestamps / bursts / identity changes (Count drops
// or holds while every index shifts). A pinned-at-bottom view never noticed (StickToBottom re-pins), but a reader
// SCROLLED UP kept the same pixel offset over a model whose indices now point at NEWER messages → the view jumped down
// toward the bottom, intermittently (only once the ring had filled, and only on one of those rebuild paths).
//
// So, when RowId is supplied: every apply remembers the topmost-visible row (index + id). On the next apply, if Count
// dropped or that index now holds a different id, find the id's new index (bounded search around the index shifted by
// the Count delta) and put that row back at the same screen offset, using the current offsets/estimates; it refines
// through the normal latch + anchor compensation afterwards. If the row is gone after a Count drop (trimmed away with
// the front of the list) the view goes to the top — the oldest surviving rows are the ones right after it. Not found
// otherwise → the old behaviour. RowId == null never reaches any of this (byte-identical).
//
// The anchor is the TRUE topmost-visible row (TopVisibleIndex), not _lastFirst: FirstIndex clamps the pool window to
// the final rows, so within ~one pool of the bottom _lastFirst sits ABOVE the viewport and rows between it and the view
// top that re-latch (all of them, after a Count-drop InvalidateHeights) would shift the view. RowId mode only, so the
// RowId == null anchor is unchanged.
internal sealed partial class WindowBuilder
{
    internal sealed partial class VariableListBinding
    {
        private int _anchorIdx = -1;   // topmost-visible data index at the end of the last apply (RowId mode; -1 = none)
        private long _anchorId;        // RowId(_anchorIdx) at that moment (0 = none — StableRowId-style "no row")

        // The index Apply anchors the view to: the identity anchor's row in RowId mode, else the historical window first.
        private int AnchorIndex() => RowId != null && _anchorIdx >= 0 ? _anchorIdx : (_lastFirst > 0 ? _lastFirst : 0);

        // Did the data under the anchor move? True with the anchor's NEW index (or -1 = gone → go to the top) when Count
        // dropped or the anchor index now holds a different row; false = mapping unchanged (or not found without a Count
        // drop) → Apply keeps its index-based behaviour. One RowId call per apply when nothing moved.
        private bool IdentityAnchor(int count, int anchorIdx, out int newIdx)
        {
            newIdx = anchorIdx;
            if (RowId == null || _anchorId == 0 || _lastCount < 0 || anchorIdx != _anchorIdx) return false;
            var dropped = count < _lastCount;
            // Same row still at the same index: nothing moved — unless Count dropped (a tail drop), where the heights were
            // just invalidated, so re-place the row on the new (estimate) offsets instead of keeping the raw pixel offset.
            if (anchorIdx < count && RowId(anchorIdx) == _anchorId) return dropped;
            // Expected spot = old index shifted by the Count delta (exact for a pure front trim); the radius covers the
            // rows appended in the same apply and a rebuild's front-drop/append mix. Only runs on a remap event.
            newIdx = VariableListMath.FindRowById(RowId, _anchorId, count, anchorIdx + (count - _lastCount), Mathf.Max(64, 4 * Slots.Length));
            return newIdx >= 0 || dropped;
        }

        // End of apply: remember the topmost-visible row for the next apply's identity check (RowId mode only).
        private void RecordAnchor(float scrollY, int first, int count)
        {
            if (RowId == null) return;
            _anchorIdx = VariableListMath.TopVisibleIndex(scrollY, _off, count, first);
            _anchorId = _anchorIdx >= 0 ? RowId(_anchorIdx) : 0L;
        }
    }
}
