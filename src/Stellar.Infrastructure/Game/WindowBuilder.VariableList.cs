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
// With RowId, a scrolled-up reader is also anchored by IDENTITY: a trim/rebuild that remaps indices under them keeps
// the same row at the same screen offset instead of the same pixel offset (which showed newer messages). See .Anchor.cs.
//
// Resize: a StickToBottom view that is pinned to the bottom stays pinned as the window grows OR shrinks. The
// "was I at the bottom?" test is judged against the PREVIOUS apply's viewport height (the size scrollY was set
// under), not this apply's — because on a resize viewportH changes while scrollY is still the value set under
// the old height. Judging a SHRINK against the new (smaller) height would raise MaxScroll above the old scrollY,
// read "not at bottom", and strand the view above the new bottom (it appears to scroll up); the previous-height
// comparison keeps wasAtBottom true so it re-pins to the new (smaller) maxScroll. See _lastViewportH.
// A CORNER resize also changes the WIDTH, which calls InvalidateHeights (rows re-wrap → every cached height
// dropped). That path CARRIES the running average into _estSeed before zeroing the accumulators, so Estimate()
// stays ≈ the real per-row height (~80px) instead of collapsing to the ~28px Baseline seed — keeping total/maxScroll
// ≈ right so the at-bottom re-pin lands on the TRUE bottom, not in the MIDDLE of the content. See _estSeed /
// InvalidateHeights (without it a corner-resize from the bottom jumped the view up to old messages).
//
// Consumer hooks (2.26.0): OnAtBottomChanged reports the SAME pin decision StickyAtBottom makes (one definition, so
// a "new messages" bar can never disagree with the auto-follow), and ScrollToBottomRequest forces that decision to
// true for one apply — the existing pinned branch then follows unmeasured bottom rows as they latch. See StickyAtBottom.
//
// First-build cap (2.26.0): at most MaxUnlatchedRows not-yet-latched rows are ACTIVE (bound + measured) at once; the
// rest of the window's unlatched slots are SetActive(false) until a latch frees budget. An unlatched row is parked
// off-screen anyway, so nothing visible changes except that a big batch (login history, tab switch) fills in over a
// few applies instead of binding ~40 bubbles in ONE apply — every value binding skips an inactive slot, and the
// slot's Conds are gated on it too (CondBinding.Gate), so the lazy branch builds, TMP glyph rasterization, avatar
// polls and bubble width-solves of the deferred rows all move to later applies. Rows nearest the anchor go first
// (the bottom when pinned). See PlanAdmission (.Measure.cs).
//
// Off-viewport cull (2.26.1): the pool window is ~viewport + a large buffer (ChatTools: 48 slots for ~8-10 visible
// rows), and every in-window slot used to stay ACTIVE — each is clipped by the viewport's RectMask2D, but
// PerformClipping still walks every active maskable every frame and every active CanvasRenderer is batched (measured:
// 246 → 835 Stellar CanvasRenderers with the chat open, clipCull 0.15 → 0.6 ms, ~115 → 100 FPS). So a LATCHED row
// whose placed rect lies wholly outside the viewport ± a margin is SetActive(false); it keeps its slot (_slotLogical)
// and its latch. Unlatched rows are never culled (measuring needs activeSelf — they stay parked off-screen as above).
// Applies run at ~10 Hz while ScrollRect moves the content every frame, so the margin covers one apply of travel
// (≥ 2 rows / 160 px + the fling velocity × 0.25 s). Reveal happens inside Apply, BEFORE the Conds/value passes, so a
// re-activated slot is rebound the same tick (no stale frame). See .Cull.cs.
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
            // Conds built inside this slot (now or in a later lazy branch build) defer while the slot is inactive —
            // the first-build cap keeps over-budget slots inactive and their lazy branches must not build early.
            var prevGate = _condGate;
            _condGate = slot;
            try { BuildElement(v.Pool[i], slot.transform, token); }
            finally { _condGate = prevGate; }
            slots[i] = srt;
        }

        token.VirtualLists.Add(new VariableListBinding
        {
            Sr = scrollRect, Content = content, Viewport = viewport, Slots = slots,
            Count = v.Count, OnWindow = v.OnWindow, ResetScroll = v.ResetScroll,
            Baseline = baseline, StickToBottom = v.StickToBottom, RowId = v.RowId, RowRevision = v.RowRevision, StableSlots = v.StableSlots,
            // ScrollToBottomRequest: the build-time value is the BASELINE, not a request (mirrors InputElement.FocusRequest).
            OnAtBottomChanged = v.OnAtBottomChanged, ScrollToBottomRequest = v.ScrollToBottomRequest,
            LastScrollRequest = v.ScrollToBottomRequest?.Invoke() ?? 0,
        });
    }

    // Poll-diffed measured-height virtual list. Each Apply: measures what the slots currently show (self-healing),
    // rebuilds the cumulative offsets when the model changed, chooses a stable scroll position, then repositions +
    // activates the pooled slots over the visible window. Returns true only when Count changed (the content extent
    // moved) — offset-driven repositioning is absolute and needs no window-level layout rebuild.
    internal sealed partial class VariableListBinding : IVirtualListBinding
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
        public bool StableSlots;   // VirtualListElement.StableSlots: row l lives in slot l % pool (see SlotOf, .Measure.cs)
        public Func<int, long>? RowId;  // VirtualListElement.RowId: data index → stable per-message id (null = never re-verify a latch)
        public Func<int, long>? RowRevision;   // VirtualListElement.RowRevision: data index → content revision (null = a latch is never re-measured in place)
        public Action<bool>? OnAtBottomChanged;   // VirtualListElement.OnAtBottomChanged: told whenever the StickToBottom pin decision flips
        public Func<int>? ScrollToBottomRequest;  // VirtualListElement.ScrollToBottomRequest: a CHANGE of this token = jump to bottom + re-pin
        public int LastScrollRequest;             // last-seen token (seeded at build, so a (re)mount never jumps by itself)
        private int _reportedAtBottom = -1;       // last value handed to OnAtBottomChanged (-1 = never reported → the first report always fires)
        private bool _jumpReq;                    // a ScrollToBottomRequest is being executed THIS apply (zero the fling velocity with the jump)

        private float[] _h = Array.Empty<float>();        // measured heights, keyed by data index
        private bool[] _measured = Array.Empty<bool>();    // has index i been LATCHED (final — never re-measured)?
        private float[] _provH = Array.Empty<float>();     // last apply's provisional (unconfirmed) measured height, by index
        private bool[] _prov = Array.Empty<bool>();        // a provisional measurement exists for index i, awaiting a 2nd-reading confirm
        private long[] _hId = Array.Empty<long>();         // RowId the cached _h[i] was measured FOR (only read/written when RowId != null; stamped on latch)
        private bool[] _verify = Array.Empty<bool>();      // index i's identity changed → re-measure it ONE apply later (after its slot's content caught up) before trusting the latch
        private long[] _hRev = Array.Empty<long>();        // RowRevision the cached _h[i] was measured AT (only used when RowRevision != null; stamped on latch)
        private bool[] _remeasure = Array.Empty<bool>();   // index i's revision changed → re-measure it in place (stays latched + on-screen until the new height confirms)
        private float[] _off = Array.Empty<float>();       // cumulative top offsets, length ≥ count+1
        private int[] _slotLogical = Array.Empty<int>();   // data index each pool slot currently shows (-1 = hidden)
        private float _sum; private int _n;                // running sum/count of measured heights → average estimate
        private float _estSeed;                            // last good average, CARRIED across a width/count InvalidateHeights (same content re-wraps) so Estimate() stays ≈ real while heights re-measure → total/maxScroll stay ≈ right → a bottom-pinned view re-pins to the TRUE bottom after a resize, not the collapsed Baseline. 0 = none (ResetState clears it: new tab = unknown heights)
        private float _lastViewportW = -1f;                // last positive viewport width; a change re-wraps rows → drop the height cache
        private float _lastViewportH = -1f;   // viewport height at the PREVIOUS apply; the at-bottom test uses the size scrollY was set under, so a resize keeps a bottom-pinned view pinned (see header "Resize")
        private bool _pinnedAtBottom = true;   // sticky "glued to bottom" latch: refreshed ONLY while the viewport is stable, TRUSTED across a viewport-height change so a multi-frame layout jolt (a popover/drawer opening below the log, shrinking it, then growing it back) can't strand a bottom-pinned view above the new bottom. Respects a scrolled-up reader (false → no yank). See StickyAtBottom.
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
        // Identity re-verify tolerance (RowId mode): when the message under a latched row changes, the latch is
        // dropped only if the new message's measured height differs by MORE than this. Far above StableEps/sub-pixel
        // so a row whose replacement is the same height keeps its latch (no re-flow/blank on an ordinary ring shift),
        // yet well below a line-height so a text↔sticker or 1↔2-line swap trips it. Only used when RowId != null.
        private const float VerifyEps = 10f;

        public bool Apply()
        {
            if (Content == null || !Content.gameObject.activeInHierarchy) return false;
            EnsureSlotMap();
            var count = Count(); if (count < 0) count = 0; var pool = Slots.Length;

            var reset = ResetScroll?.Invoke() == true;
            if (reset) ResetState();

            var viewportH = Viewport != null ? Viewport.rect.height : 0f;
            var scrollY = Mathf.Max(0f, Content.anchoredPosition.y);
            // Captured from the OLD model (before any rebuild) for anchoring + the at-bottom decision.
            var anchorIdx = AnchorIndex();   // RowId mode: the identity anchor's row (.Anchor.cs); else the last window first
            var anchorTopOld = (_lastCount >= 0 && anchorIdx < _off.Length) ? _off[anchorIdx] : 0f;
            var oldTotal = (_lastCount >= 0 && _lastCount < _off.Length) ? _off[_lastCount] : 0f;
            var wasAtBottom = StickyAtBottom(scrollY, oldTotal, viewportH);

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
            else if (IdentityAnchor(count, anchorIdx, out var idIdx)) scrollY = idIdx < 0 ? 0f : Mathf.Clamp(_off[idIdx] + (scrollY - anchorTopOld), 0f, maxScroll);   // rows remapped under a scrolled-up reader → keep the SAME row (.Anchor.cs)
            else if (measuredChanged && anchorIdx <= count) scrollY = Mathf.Clamp(_off[anchorIdx] + (scrollY - anchorTopOld), 0f, maxScroll);
            else scrollY = Mathf.Min(scrollY, maxScroll);
            if (!Mathf.Approximately(scrollY, Content.anchoredPosition.y)) SetScroll(scrollY, reset || _firstApply || _jumpReq);

            var first = VariableListMath.FirstIndex(scrollY, _off, count, pool); _viewY = scrollY; _viewH = viewportH;   // view rect for the cull (.Cull.cs)
            // wasAtBottom = the pin decision → bottom-up admission while pinned. Same `first` → Cull re-diffs shown rows.
            if (first != _lastFirst || countChanged || measuredChanged || reset) Reposition(first, count, pool, wasAtBottom); else Cull(first, count, pool, wasAtBottom);

            RecordAnchor(scrollY, first, count); _lastFirst = first; _lastCount = count; _firstApply = false; _jumpReq = false; if (viewportH > 0f) _lastViewportH = viewportH;
            return countChanged;
        }

        // "Is the view glued to the bottom?" — the StickToBottom follow decision, with a sticky latch that survives
        // a VIEWPORT-HEIGHT change. The base test is the historical one: at/near the bottom judged against the
        // PREVIOUS apply's height (so a window resize stays pinned — see header "Resize"). On a STABLE-viewport apply
        // that value equals the live at-bottom status, so returning it is byte-identical to the old inline test.
        //
        // Why the latch: a popover/drawer that opens BELOW the log (the ChatTools sticker picker is a
        // ConditionalElement whose grid builds LAZILY on first open — WindowBuilder.Layout.cs) shrinks the flex log
        // viewport over SEVERAL frames, then grows it back on close, and the ScrollRect re-clamps during those
        // rebuilds. Across that multi-frame jolt one intermediate apply can read the live scroll as "not at bottom"
        // and strand the view above the new bottom → the just-sent sticker lands off-screen below (first-open was the
        // worst case: the lazy build is the biggest jolt, and it coincides with the self-send append). So refresh the
        // latch ONLY while the viewport is stable (the sole moment the live scroll is trustworthy) and TRUST the
        // latched value on any apply where the height changed. A scrolled-up reader latches false → an open/close
        // never yanks them down. This fixes any resize-the-log popover/drawer, not just the sticker picker.
        //
        // This is also the ONE place "at the bottom" is decided for the consumer: OnAtBottomChanged is fed the value
        // returned here (so the plugin's "new messages" bar and the pin can never disagree — content that fits makes
        // MaxScroll 0, which always passes), and a ScrollToBottomRequest simply FORCES the decision to true. Forcing
        // the pin (rather than a one-off SetScroll) is what makes the jump land on the TRUE bottom: the rows near the
        // bottom are usually unmeasured (estimate-sized) when we jump, and as each latches its real height the content
        // grows — the pinned branch then re-pins to the new maxScroll every apply, exactly as for an append while
        // already at the bottom. Next apply the live scroll sits at the maxScroll we set, so the re-derived test agrees.
        private bool StickyAtBottom(float scrollY, float oldTotal, float viewportH)
        {
            var jump = TakeScrollRequest();
            if (!StickToBottom) return jump;   // non-stick list: a one-shot jump to the current (estimated) bottom
            if (jump) { _pinnedAtBottom = true; return ReportAtBottom(true); }
            var atBottom = _firstApply || _lastCount < 0
                || scrollY >= VariableListMath.MaxScroll(oldTotal, _lastViewportH > 0f ? _lastViewportH : viewportH) - BottomEps;
            // Viewport height changed since the last apply → in the middle of a jolt; the live scroll is unreliable,
            // so hold the last stable decision instead of re-deriving (and possibly flipping) it.
            if (_lastViewportH > 0f && viewportH > 0f && !Mathf.Approximately(viewportH, _lastViewportH)) return ReportAtBottom(_pinnedAtBottom);
            _pinnedAtBottom = atBottom;
            return ReportAtBottom(atBottom);
        }

        // ScrollToBottomRequest edge: true once per CHANGE of the consumer's counter token (vs the build-time seed).
        private bool TakeScrollRequest()
        {
            if (ScrollToBottomRequest == null) return false;
            var v = ScrollToBottomRequest();
            if (v == LastScrollRequest) return false;
            LastScrollRequest = v; _jumpReq = true;   // _jumpReq → Apply zeroes the fling velocity with the jump
            return true;
        }

        // Hand the at-bottom decision to OnAtBottomChanged on a FLIP only (the first report always fires = the initial
        // state). Recorded BEFORE invoking so a throwing consumer callback is not re-invoked every apply.
        private bool ReportAtBottom(bool atBottom)
        {
            var v = atBottom ? 1 : 0;
            if (OnAtBottomChanged == null || v == _reportedAtBottom) return atBottom;
            _reportedAtBottom = v;
            OnAtBottomChanged(atBottom);
            return atBottom;
        }

        // The running estimate = average of currently-measured rows. When nothing is measured right now, fall back to
        // the carried seed (_estSeed, the average from just before a width/count invalidation) if present, else Baseline.
        // Used for every not-yet-shown row, so the total/scrollbar is approximate immediately and tightens as rows measure.
        private float Estimate() => _n > 0 ? _sum / _n : (_estSeed > 0f ? _estSeed : Baseline);

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
        //
        // First-build cap: PlanAdmission decides which in-range slots are shown at all. A slot it holds back is treated
        // exactly like a past-the-end slot (inactive, unbound, its provisional dropped) — it was going to be parked
        // off-screen anyway — and is admitted by a later Reposition, which every latch triggers (measuredChanged).
        //
        // Off-viewport cull (2.26.1): a shown LATCHED row outside the view ± margin is made inactive (ShowLatched,
        // .Cull.cs) but is otherwise handled exactly like a shown row — still sized, placed at its true offset and
        // recorded in _slotLogical — so Cull can re-reveal it later without a Reposition. Unlatched rows never cull.
        private void Reposition(int first, int count, int pool, bool bottomFirst)
        {
            OnWindow(first);
            PlanAdmission(first, count, pool, bottomFirst);
            BeginCullPass();   // margin + "last culled at" key for this view (.Cull.cs)
            for (var d = 0; d < pool; d++)   // d = row offset in the window; SlotOf picks the slot that shows it
            {
                var k = SlotOf(first, d, pool);
                var rt = Slots[k];
                if (rt == null) continue;
                var logical = first + d;
                var show = logical < count && _admit[k];
                if (show && _measured[logical]) ShowLatched(logical, rt, InView(logical));
                else if (rt.gameObject.activeSelf != show) rt.gameObject.SetActive(show);
                // A slot leaving a data index DROPS that index's provisional (unconfirmed) reading, so the two-read
                // stable-confirm can only ever fire from readings taken on CONSECUTIVE applies while the row stayed
                // continuously shown in ONE slot — never from two unrelated transient readings bridged across the row
                // scrolling out and back in (a provisional was keyed by data index and otherwise outlived the recycle).
                // A CONFIRMED latch (_measured/_h) is intentionally KEPT across recycle so a row that scrolls back in
                // reuses its height with no estimate→real pop (file header "scroll stability"); only the in-flight
                // provisional is cleared. NB: this does NOT address a stale latch whose underlying message changed in
                // place under a stationary slot (the rolling-buffer index remap, header limit #3) — see commit msg.
                var bound = show ? logical : -1;
                var leaving = _slotLogical[k];
                if (leaving >= 0 && leaving != bound && leaving < _prov.Length) { _prov[leaving] = false; _provH[leaving] = 0f; _tries[leaving] = 0; }
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
            var hid = new long[cap]; var vf = new bool[cap];
            Array.Copy(_h, h, _h.Length); Array.Copy(_measured, m, _measured.Length);
            Array.Copy(_provH, ph, _provH.Length); Array.Copy(_prov, p, _prov.Length);
            Array.Copy(_hId, hid, _hId.Length); Array.Copy(_verify, vf, _verify.Length);
            var sn = new bool[cap]; Array.Copy(_seen, sn, _seen.Length); _seen = sn;   // first-build cap exemption (.Measure.cs)
            var hr = new long[cap]; Array.Copy(_hRev, hr, _hRev.Length); _hRev = hr;   // RowRevision stamps (.Measure.cs)
            var rm = new bool[cap]; Array.Copy(_remeasure, rm, _remeasure.Length); _remeasure = rm;
            var tr = new byte[cap]; Array.Copy(_tries, tr, _tries.Length); _tries = tr;   // failed-confirm counts (.Measure.cs)
            _h = h; _measured = m; _provH = ph; _prov = p; _hId = hid; _verify = vf; _off = new float[cap + 1];
            _offDirty = true;
        }

        // Drop every cached height but KEEP the scroll position and slot mapping. Used when a change makes stored
        // heights stale yet the view should stay put: a viewport-WIDTH change (rows re-wrap) or a Count DECREASE
        // (shrink / filter / rolling-buffer front-drop → data indices now point at different rows). Active slots
        // re-measure the SAME apply (they are unmeasured again); off-screen rows fall back to the estimate until
        // shown; the topmost-visible anchor compensation keeps the view from jumping. Softer than ResetState,
        // which also re-pins the scroll and forgets the window.
        // The running average is CAPTURED into _estSeed BEFORE zeroing the accumulators: the SAME content merely
        // re-wraps, so the old average is a good seed and Estimate() stays ≈ real while heights re-measure. Without
        // it Estimate() collapsed to the ~28px Baseline, total/maxScroll under-estimated, and a bottom-pinned
        // StickToBottom view re-pinned into the MIDDLE of the content (the corner-resize "jumped up" bug).
        private void InvalidateHeights()
        {
            Array.Clear(_measured, 0, _measured.Length);
            Array.Clear(_prov, 0, _prov.Length);   // rows re-run the stable-confirm from scratch at the new width
            Array.Clear(_verify, 0, _verify.Length);   // no latch survives → any pending identity re-verify is moot (_hId re-stamped on re-latch)
            Array.Clear(_remeasure, 0, _remeasure.Length);   // likewise any in-place re-measure (_hRev re-stamped on re-latch)
            Array.Clear(_tries, 0, _tries.Length);   // fresh stable-confirm → fresh try budget
            _estSeed = Estimate();   // carry the real average across the clear (see comment above) — MUST precede the zeroing
            _sum = 0f; _n = 0; _offDirty = true; _cullKeyH = -1f;   // force the next Cull: a culled row that just lost its latch must re-activate to re-measure
        }

        // A reset (filter/tab switch via ResetScroll) forgets the model: data indices now map to different rows,
        // so stored heights are stale. Clearing re-measures from the next shown frame; the scroll re-pins. Unlike
        // InvalidateHeights this is genuinely NEW content, so _estSeed is zeroed too (fall back to Baseline — do NOT
        // carry an old tab's average into a new tab whose rows may wrap to entirely different heights).
        private void ResetState()
        {
            Array.Clear(_measured, 0, _measured.Length);
            Array.Clear(_prov, 0, _prov.Length);
            Array.Clear(_hId, 0, _hId.Length); Array.Clear(_verify, 0, _verify.Length);   // genuinely new content → forget identities + pending re-verifies
            Array.Clear(_remeasure, 0, _remeasure.Length);
            Array.Clear(_tries, 0, _tries.Length);
            Array.Clear(_seen, 0, _seen.Length);   // new content → no row is exempt from the first-build cap
            for (var k = 0; k < _slotLogical.Length; k++) _slotLogical[k] = -1;
            _sum = 0f; _n = 0; _estSeed = 0f; _offDirty = true; _lastViewportW = -1f; _lastViewportH = -1f;
            _lastFirst = -1; _lastCount = -1; _firstApply = true; _pinnedAtBottom = true; _anchorIdx = -1; _anchorId = 0L;   // a reset re-pins to bottom (StickToBottom) → latch starts glued
        }
    }
}
