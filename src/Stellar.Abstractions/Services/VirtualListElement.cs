using System;
using System.Collections.Generic;

namespace Stellar.Abstractions.Services;

// Split out of WindowElement.cs for the 500-LoC file gate (same namespace + assembly — a pure move, no API change).

/// <summary>Scroll-windowed variable list: renders a small fixed <paramref name="Pool"/> of K rows
/// (K ≈ viewport rows + a few margin rows) over a logical list of <paramref name="Count"/>() items that may
/// be FAR larger than K. The framework sizes a scroll content spacer to Count()*RowHeight, tracks the scroll
/// offset, computes the first visible logical index, calls <paramref name="OnWindow"/>(first) BEFORE pulling
/// any slot values (so the plugin's row Funcs resolve item first+slotIndex), positions the pool rows at their
/// logical offset, and SetActive-shows only rows whose logical index &lt; Count(). Rows are uniform
/// fixed-height (<paramref name="RowHeight"/>) UNLESS <see cref="VariableHeight"/> is set — see below. Use for
/// large pickers; the small <see cref="ListElement"/> (eager full pool) stays for short lists.
/// <paramref name="Height"/> is the viewport height.</summary>
public sealed record VirtualListElement(
    Func<int>  Count,
    float      RowHeight,
    IReadOnlyList<HudElement> Pool,
    Action<int> OnWindow,
    float      Height = 200f) : HudElement
{
    /// <summary>When non-null, polled each refresh — returning true resets the scroll offset (to the top, or to
    /// the bottom when <see cref="StickToBottom"/> is set) and, in <see cref="VariableHeight"/> mode, forgets the
    /// measured-height model (row ↔ data-index associations have changed, e.g. a filter switch), so heights
    /// re-measure from the next shown frame.</summary>
    public Func<bool>? ResetScroll { get; init; }

    /// <summary>Opt into VARIABLE-height rows: each pooled slot is MEASURED (uGUI preferred height, exactly as a
    /// <c>BubbleElement</c>/<c>ContentSizeFitter</c> fits itself) after it is bound to a data index, and the list
    /// keeps a per-index height model + cumulative offsets instead of assuming uniform rows. Un-measured rows use
    /// <see cref="RowHeight"/> as a baseline estimate (a running average of the measured rows once any exist), so
    /// the scrollbar is approximately right immediately and refines as rows scroll into view. Positioning uses the
    /// cumulative offsets; the content height is derived from the model. Default <c>false</c> = the original
    /// uniform path, byte-identical for existing consumers. The pool must still be sized to cover the viewport
    /// (≈ viewport ÷ the SHORTEST row + a buffer) — a pool smaller than the on-screen row count leaves a gap, same
    /// as the uniform list. An <c>init</c> property (not a ctor param) so the record stays binary-compatible.</summary>
    public bool VariableHeight { get; init; }

    /// <summary>Newest-at-bottom behaviour (chat log): the list starts scrolled to the BOTTOM, and when the user is
    /// already at the bottom a grown <see cref="Count"/> (a new row appended) keeps them pinned there; if they have
    /// scrolled up, an append does NOT yank them down — their position is preserved. Only governs the SCROLL
    /// position (when the content exceeds the viewport); short content sits top-aligned like any scroll. Default
    /// <c>false</c> = start at the top. An <c>init</c> property (binary-compatible). Independent of
    /// <see cref="VariableHeight"/>, though the two are used together for a wrapping chat history.</summary>
    public bool StickToBottom { get; init; }

    /// <summary>Optional stable per-ROW identity (data index → a reasonably-collision-free id for the MESSAGE/item
    /// currently at that index), used ONLY in <see cref="VariableHeight"/> mode to keep the measured-height cache
    /// honest when the data source re-maps indices WITHOUT changing <see cref="Count"/>. A latched height is keyed
    /// by data index; if the consumer's backing store is a rolling buffer that front-drops at capacity (e.g. a
    /// capped chat ring), every new item shifts every index by one while Count stays constant, so a cached height
    /// silently belongs to a different item than the index now points at — a tall row's slot inherits a short row's
    /// latch and overflows. When supplied, the list re-verifies a latched row's height the moment the identity under
    /// it changes and drops the latch only if the height is grossly wrong (an item whose replacement happens to be
    /// the same height keeps its latch, so no needless re-flow); a row whose identity is unchanged is still skipped
    /// with no re-measure, so scrolling stays cheap. Null (default) = today's behaviour exactly (a latched row is
    /// never re-measured). Fed the SAME data index the pool slots resolve (window-independent), NOT a pool-slot
    /// offset. An <c>init</c> property (binary-compatible).</summary>
    public Func<int, long>? RowId { get; init; }

    /// <summary>Optional — invoked on the main thread whenever the list's "at the bottom" state FLIPS, and once with
    /// the initial state after the list first lays out (and again after a window rebuild). "At the bottom" is exactly
    /// the decision <see cref="StickToBottom"/> uses to keep the view pinned on an append, so the two never disagree:
    /// within a couple of px of the bottom, and content that fits inside the viewport counts as at the bottom. Typical
    /// use: a chat "↓ N new messages" bar shown while the reader is scrolled up. Reported only for a
    /// <see cref="VariableHeight"/> + <see cref="StickToBottom"/> list (never invoked otherwise). The state is read
    /// once per window refresh, so a flip reaches the callback up to one refresh late. Default null = unchanged. An
    /// <c>init</c> property (binary-compatible).</summary>
    public Action<bool>? OnAtBottomChanged { get; init; }

    /// <summary>Optional counter token: when the returned value CHANGES (vs the value seen when the list was built — a
    /// fresh/rebuilt list never jumps on its own), the list scrolls to the newest row and, with
    /// <see cref="StickToBottom"/>, re-engages the bottom pin, so rows below that were not yet measured (and grow the
    /// content as they latch their real heights) are followed until the view settles on the true bottom. Keep an int,
    /// increment it in the click/send handler, and bind <c>ScrollToBottomRequest = () =&gt; _seq</c>. Without
    /// <see cref="StickToBottom"/> it is a one-shot jump to the current (estimated) bottom. Honoured in
    /// <see cref="VariableHeight"/> mode only. Default null = unchanged. An <c>init</c> property (binary-compatible).</summary>
    public Func<int>? ScrollToBottomRequest { get; init; }

    /// <summary>Optional per-row CONTENT revision (data index → a number the consumer changes whenever that row's
    /// content changes in a way that can change its height — e.g. a chat message swapping to its translation, or a
    /// status line appearing under it). <see cref="VariableHeight"/> mode only. A row's measured height is normally
    /// latched for good; when its revision differs from the one it was latched under, the list re-measures that one
    /// row IN PLACE (it stays on screen at its old height meanwhile — no blank, no park) and commits the new height
    /// once two consecutive readings agree, then re-flows the rows below (a bottom-pinned
    /// <see cref="StickToBottom"/> view stays pinned; otherwise the topmost visible row holds still). Return the same
    /// value (e.g. 0) for rows that never change. Independent of <see cref="RowId"/> (identity) — use both together.
    /// Default null = unchanged. An <c>init</c> property (binary-compatible).</summary>
    public Func<int, long>? RowRevision { get; init; }

    /// <summary>Optional — keep each data row in the SAME pool slot for as long as it stays in the window
    /// (<see cref="VariableHeight"/> mode only). Normally pool slot <c>k</c> shows row <c>first + k</c>, so whenever
    /// <c>first</c> moves by one — every append to a bottom-pinned <see cref="StickToBottom"/> list that is longer than
    /// the pool, and every scroll step — EVERY slot is rebound to a different row (new text, new avatar, a bubble width
    /// re-solve each). With this set, row <c>l</c> lives in slot <c>l % Pool.Count</c>: a one-row shift recycles only the
    /// slot whose row left the window, and every other slot keeps its content untouched. <see cref="OnWindow"/> still
    /// reports <c>first</c>; the row Funcs must then resolve slot <c>k</c> through
    /// <see cref="VariableListMath.StableSlotRow"/>(first, k, Pool.Count) instead of <c>first + k</c>. Default
    /// <c>false</c> = unchanged. An <c>init</c> property (binary-compatible).</summary>
    public bool StableSlots { get; init; }
}
