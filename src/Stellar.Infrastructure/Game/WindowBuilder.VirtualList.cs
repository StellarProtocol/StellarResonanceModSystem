using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Scroll-windowed virtual list (Phase E Tasks A3 + A4). A small fixed pool of absolutely-positioned slot rows
// is recycled (repositioned + rebound) by VirtualListBinding as the user scrolls, so a logical list of
// thousands renders with ~K GameObjects. Split into its own partial to keep WindowBuilder.cs +
// WindowBuilder.Bindings.cs under the 500-LoC file gate.
//
// Off-viewport cull (2.26.1): slots inside the pool window but wholly outside the viewport ± a margin are made
// inactive (they used to all stay active, clipped yet still clip-walked + batched every frame). See VirtualListBinding.
internal sealed partial class WindowBuilder
{
    // Sibling to BuildScroll's plumbing: builds a masked viewport + a content rect + a vertical ScrollRect
    // (Clamped) + the thin themed scrollbar, and returns the three the caller wires content into. Mirrors the
    // viewport/content/RectMask2D/raycast-catcher/ScrollRect/scrollbar construction in BuildScroll verbatim, but
    // does NOT add a VerticalLayoutGroup or ContentSizeFitter to the content — the virtual list sizes the content
    // spacer itself (Count*RowHeight) and positions its slots absolutely. BuildScroll is left untouched.
    internal (RectTransform viewport, RectTransform content, ScrollRect sr) BuildScrollViewport(Transform parent, float height, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Scroll", parent);
        var le = go.AddComponent<LayoutElement>(); le.preferredHeight = height; le.flexibleWidth = 1f; le.flexibleHeight = 1f;
        var sr = go.AddComponent<ScrollRect>(); sr.horizontal = false; sr.vertical = true; sr.movementType = ScrollRect.MovementType.Clamped;

        var viewportGo = UGuiPrimitives.NewChild("Viewport", go.transform); UGuiPrimitives.Stretch(viewportGo);
        // Inset 9px from the right edge (5px bar + 4px gap) so the scrollbar does not overlay content —
        // same fix as BuildScroll; without this the bar sits on top of right-aligned elements (toggles).
        viewportGo.GetComponent<RectTransform>().offsetMax = new Vector2(-9f, 0f);
        viewportGo.AddComponent<RectMask2D>();
        // Transparent raycast target so the mouse WHEEL has something to hit over the scroll area (the
        // EventSystem routes OnScroll up to the ScrollRect) — matches BuildScroll.
        var catcher = viewportGo.AddComponent<Image>(); catcher.color = new Color(0f, 0f, 0f, 0f); catcher.raycastTarget = true;
        sr.scrollSensitivity = 24f;

        var contentGo = UGuiPrimitives.NewChild("Content", viewportGo.transform);
        var crt = contentGo.GetComponent<RectTransform>();
        crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(1f, 1f); crt.pivot = new Vector2(0f, 1f); crt.anchoredPosition = Vector2.zero;
        crt.sizeDelta = Vector2.zero;

        var viewport = viewportGo.GetComponent<RectTransform>();
        sr.viewport = viewport; sr.content = crt;
        BuildScrollbar(sr, go, token);   // thin themed scrollbar (registers a reskin closure on token)
        RegisterScrollView?.Invoke(crt);   // crt moves during scroll → ticker drops pixelPerfect while it's moving
        return (viewport, crt, sr);
    }

    // Scroll-windowed list: a masked viewport + a tall content rect (height = Count*RowHeight) + a fixed pool
    // of K absolutely-positioned slot rows the binding recycles as the user scrolls. Slots are children of the
    // content (so they translate smoothly with the ScrollRect between rebinds); the binding repositions +
    // rebinds them when the first-index changes.
    private void BuildVirtualList(VirtualListElement v, Transform parent, WindowToken token)
    {
        // Variable-height rows (wrapping chat bubbles) take a wholly different model (measure + cumulative
        // offsets + scroll-anchoring), kept in the sibling partial WindowBuilder.VariableList.cs for the
        // file-size gate. The uniform fixed-RowHeight path below is unchanged.
        if (v.VariableHeight) { BuildVariableList(v, parent, token); return; }

        var (viewport, content, scrollRect) = BuildScrollViewport(parent, v.Height, token);
        content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f); content.anchoredPosition = Vector2.zero;

        var slots = new RectTransform[v.Pool.Count];
        for (var i = 0; i < v.Pool.Count; i++)
        {
            var slot = UGuiPrimitives.NewChild("VSlot", content);
            var srt = slot.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0f, 1f); srt.anchorMax = new Vector2(1f, 1f);
            srt.pivot = new Vector2(0.5f, 1f);
            srt.sizeDelta = new Vector2(0f, v.RowHeight);
            UGuiPrimitives.AddLayout(slot, gap: 0f, columns: UGuiPrimitives.ColumnMode); ExpandColumnWidth(slot);
            BuildElement(v.Pool[i], slot.transform, token);
            slots[i] = srt;
        }

        token.VirtualLists.Add(new VirtualListBinding
        {
            Sr = scrollRect, Content = content, Viewport = viewport, Slots = slots,
            Count = v.Count, RowHeight = v.RowHeight, OnWindow = v.OnWindow, ResetScroll = v.ResetScroll,
        });
    }

    // Poll-diffed scroll-windowed list. Each Apply: reads the scroll offset, maps it to the first logical row
    // (VirtualListMath), pushes that to the plugin via OnWindow (so slot Funcs resolve snapshot[first+i] THIS
    // poll), sizes the content spacer to Count*RowHeight, and repositions + activates the K pooled slots over
    // the visible window. Returns true when the visible set changed. Runs BEFORE Conds/Texts (WindowToken.Apply).
    internal sealed class VirtualListBinding : IVirtualListBinding
    {
        public ScrollRect Sr = null!;
        public RectTransform Content = null!;
        public RectTransform[] Slots = System.Array.Empty<RectTransform>();
        public Func<int> Count = null!;
        public float RowHeight;
        public Action<int> OnWindow = null!;
        public Func<bool>? ResetScroll;
        public RectTransform? Viewport;   // off-viewport cull (2.26.1): the visible band rows are tested against
        private int _lastFirst = -1, _lastCount = -1;
        // Off-viewport cull (2.26.1) — same mechanism + reasoning as the variable list (WindowBuilder.VariableList.Cull.cs):
        // the pool is usually sized ~viewport + buffer and every in-window slot used to stay ACTIVE (clipped, but still
        // walked by RectMask2D.PerformClipping and batched every frame). A row wholly outside the viewport ± a margin
        // is SetActive(false) while keeping its position, and re-revealed here — before the Conds/value passes, so it
        // is rebound the same tick. Applies run ~10 Hz while ScrollRect moves every frame → the margin covers one apply
        // of travel. Uniform rows have no measurement, so every row can cull. Diffed; an unmoved list does no cull work.
        private const float CullMarginMinPx = 160f, CullLookaheadSec = 0.25f;
        private float _cullY = float.NaN, _cullH = -1f, _cullMargin;
        private bool _cullMoving;

        public bool Apply()
        {
            if (Content == null || !Content.gameObject.activeInHierarchy) return false;
            if (ResetScroll?.Invoke() == true)
            {
                Sr.normalizedPosition = new Vector2(0f, 1f);
                Content.anchoredPosition = Vector2.zero;
                _lastFirst = -1;
            }
            var count = Count();
            var pool = Slots.Length;
            var countChanged = count != _lastCount;
            if (countChanged) Content.sizeDelta = new Vector2(0f, VirtualListMath.ContentHeight(count, RowHeight));
            var scrollY = Mathf.Max(0f, Content.anchoredPosition.y);
            var first = VirtualListMath.FirstIndex(scrollY, RowHeight, count, pool);
            var cullDue = BeginCullPass(scrollY, Viewport != null ? Viewport.rect.height : 0f);
            // Only touch the plugin offset + slots when the visible window actually moved (first or count
            // changed) — re-pushing an unchanged window every poll re-runs the plugin's peek reconcile for
            // nothing. The reposition is self-managed (absolute anchoredPosition), so a first-only change needs
            // NO uGUI layout pass; only a count change (content extent moved) returns structuralChange=true.
            if (first != _lastFirst || countChanged)
            {
                OnWindow(first);
                for (var i = 0; i < pool; i++)
                {
                    var logical = first + i;
                    var rt = Slots[i];
                    if (rt == null) continue;
                    var show = logical < count;
                    var on = show && InView(logical);   // placed (below) even when culled, so a later reveal is a bare SetActive
                    if (rt.gameObject.activeSelf != on) rt.gameObject.SetActive(on);
                    if (show) rt.anchoredPosition = new Vector2(0f, -logical * RowHeight);
                }
                _lastFirst = first;
            }
            else if (cullDue) Cull(first, count);   // same window, the view moved → re-diff which rows show
            _lastCount = count;
            return countChanged;
        }

        // Snapshot this apply's view + margin; true iff the view (scroll Y, viewport height, moving vs at rest) changed
        // since the last call. Exact float compares are intended (an unmoved list re-reads the identical position).
        private bool BeginCullPass(float y, float h)
        {
            var vel = Sr != null ? Mathf.Abs(Sr.velocity.y) : 0f;
            var moving = vel >= 1f;
            var due = y != _cullY || h != _cullH || moving != _cullMoving;
            _cullY = y; _cullH = h; _cullMoving = moving;
            _cullMargin = Mathf.Max(2f * RowHeight, CullMarginMinPx) + vel * CullLookaheadSec;
            return due;
        }

        // Row `logical`'s rect overlaps the viewport ± margin? No viewport height yet → never cull blind.
        private bool InView(int logical)
        {
            if (_cullH <= 0f) return true;
            var top = logical * RowHeight;
            return top + RowHeight > _cullY - _cullMargin && top < _cullY + _cullH + _cullMargin;
        }

        private void Cull(int first, int count)
        {
            for (var i = 0; i < Slots.Length; i++)
            {
                var rt = Slots[i]; var logical = first + i;
                if (rt == null || logical >= count) continue;
                var on = InView(logical);
                if (rt.gameObject.activeSelf != on) rt.gameObject.SetActive(on);
            }
        }
    }
}
