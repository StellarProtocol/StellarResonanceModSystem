using System;
using Stellar.Abstractions.Domain;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Smaller WindowBuilder binding inner-classes, split out of WindowBuilder.Bindings.cs to keep that file under
// the per-file size gate. Same poll-diff pattern as the rest.
internal sealed partial class WindowBuilder
{
    // Poll-diffed backdrop for an AccentRowElement: a faint share-fraction wash (width via anchorMax.x) + a
    // role-coloured left stripe. Idle rows (unchanged share/colour) write nothing.
    internal sealed class AccentRowBinding
    {
        public RectTransform ShareRect = null!;
        public Image ShareImg = null!;
        public Image Stripe = null!;
        public Func<ColorRgba> StripeFn = null!;
        public Func<float> ShareFn = null!;
        private float _lastShare = -1f;
        private ColorRgba _lastCol;
        private bool _init;
        public void Apply()
        {
            if (ShareRect == null || !ShareRect.gameObject.activeInHierarchy) return;
            var share = Mathf.Clamp01(ShareFn());
            if (!Mathf.Approximately(share, _lastShare)) { ShareRect.anchorMax = new Vector2(share, 1f); _lastShare = share; }
            var c = StripeFn();
            if (!_init || !c.Equals(_lastCol))
            {
                Stripe.color = new Color(c.R, c.G, c.B, 1f);
                ShareImg.color = new Color(c.R, c.G, c.B, 0.12f);
                _lastCol = c; _init = true;
            }
        }
    }

    // Per-apply poll for a tile's pin-star state (texture/colour). The hover VISUAL (icon grow + brighten) is
    // driven separately by the renderer's interaction ticker; this only refreshes externally-changed state.
    internal sealed class HoverBinding { public Action? Poll; }

    // Re-syncs a text field from its Get() when the backing value changes EXTERNALLY (e.g. a chat composer
    // cleared after send, or DataInspector's ID set by a recent-lookup restore). Diffs on Get() (not the
    // field text), so it never fights live typing: while the user types, Get() either tracks the field (via
    // OnChange) or stays unchanged (no OnChange) — both leave Get()==Last so no SetText fires. The field.Text
    // guard also avoids a redundant SetText (cursor jump) when OnChange already updated the field.
    internal sealed class FieldBinding
    {
        public UGuiTextInput Field = null!;
        public Func<string> Get = null!;
        public string Last = "";
        public void Apply()
        {
            if (Field == null) return;
            var v = Get();
            if (v == Last) return;
            Last = v;
            if (Field.Text != v) Field.SetText(v);
        }
    }

    // Per-apply width solver for a BubbleElement: content-hug up to a cap, then wrap. Reads the child's NATURAL
    // (unwrapped) preferred width — which is wrap-INDEPENDENT (uGUI Text.preferredWidth ignores the current rect
    // width), so pinning the bubble narrower never feeds back and oscillates — caps it at MaxWidth (or the
    // parent's current width when MaxWidth == 0), and pins the bubble's LayoutElement to that width. A short child
    // → a narrow bubble; a long child → the bubble fixes at the cap and its wrapping Text flows to >1 line (the
    // vertical ContentSizeFitter then grows the height). Diffs on the resolved width so an unchanged bubble writes
    // nothing. Setting preferredWidth marks the layout dirty, so Unity rebuilds it on the next render frame.
    internal sealed class BubbleBinding
    {
        public LayoutElement Le = null!;       // on the bubble GO (the width driver)
        public RectTransform Child = null!;    // the single content child (measured)
        public RectTransform? Parent;          // bubble's parent (its width = the cap when MaxWidth == 0)
        public float MaxWidth;                  // 0 = clamp to Parent's current width
        public float PadX;                      // total horizontal padding (left + right)
        // Per-refresh fill colour (BubbleElement.FillColorFn). FillGraphic/FillColorFn left null → the bubble keeps
        // its build-time colour (the static FillColor / themed default painted once in AddBubbleBackground) with no
        // override machinery — a bubble that doesn't set FillColorFn is byte-for-byte unchanged. Mirrors
        // BarBinding.ApplyFillColor: Override every poll a colour is returned (so a theme reskin never wins),
        // RestoreDefault once it goes back to null. The restore-default is the fill's build-time colour captured on
        // the first poll (= the static fill AddBubbleBackground painted).
        public Graphic? FillGraphic; public Func<ColorRgba?>? FillColorFn;
        private ColorOverrideTracker _fillColor; private Color? _fillBuildColor;
        private float _last = -1f;
        public void Apply()
        {
            if (Le == null || Child == null) return;
            // Fill colour is polled BEFORE the width-diff early-return below, so a live re-tint lands even on a
            // tick where the hugged width is unchanged (the common case for a fixed-width segmented tab).
            if (FillGraphic != null && FillColorFn != null) ApplyFillColor(FillColorFn());
            var measured = LayoutUtility.GetPreferredWidth(Child);
            if (measured <= 0f)
            {
                // Recycled scroll rebind: the child's text layout isn't rebuilt for the new content yet, so a stale 0 read
                // would pin the bubble to ~PadX and the wrapping Text would break to one char per line for a frame (the
                // "one char per line" flicker, previously masked by the removed ContentSizeFitter). Force a one-shot rebuild
                // of JUST this child so the measurement is correct THIS apply. Only fires when the read is unready (rebind
                // frames) — a settled bubble reports a real width and never enters here, so no per-frame cost is added.
                LayoutRebuilder.ForceRebuildLayoutImmediate(Child);
                measured = LayoutUtility.GetPreferredWidth(Child);
                if (measured <= 0f) return;   // still unmeasurable (e.g. child inactive this frame) → keep the last pinned width, don't collapse
            }
            var natural = measured + PadX;
            // Cap: an explicit MaxWidth, else the parent's laid-out width. Before the parent is sized (first seed)
            // fall back to the natural width — the bubble hugs this pass and corrects once the parent lays out.
            var cap = MaxWidth > 0f ? MaxWidth : (Parent != null ? Parent.rect.width : natural);
            if (cap <= 0f) cap = natural;
            var w = Mathf.Max(0f, Mathf.Min(natural, cap));
            if (Mathf.Approximately(w, _last)) return;
            Le.minWidth = Le.preferredWidth = w; Le.flexibleWidth = 0f;
            _last = w;
        }

        private void ApplyFillColor(ColorRgba? v)
        {
            _fillBuildColor ??= FillGraphic!.color;   // the static fill painted at build = the restore-default
            switch (_fillColor.Next(v.HasValue))
            {
                case ColorStep.Override: FillGraphic!.color = new Color(v!.Value.R, v.Value.G, v.Value.B, v.Value.A); break;
                case ColorStep.RestoreDefault: FillGraphic!.color = _fillBuildColor.Value; break;
            }
        }
    }

    // Per-apply re-tint for a SelectableElement row. Diffs on a 0/1/2 state (rest/hover/selected) like the
    // other bindings, so a no-change poll writes nothing — avoids marking the bg Image vertex-dirty (a
    // redundant canvas rebatch) every Apply for every idle row. HoverState is pushed by the ticker hover hook;
    // Selected() is polled. ForceRepaint() (resets the diff) is used by the hover hook, the initial paint, and
    // the theme-reskin (which recomputes Rest/Hover/On).
    internal sealed class SelectableBinding
    {
        public Image Bg = null!;
        public Image? Bar;            // optional "active tab" accent underline (SelectableElement.SelectedBar); null = none
        public Func<bool>? SelectedFn;
        public Color Rest, Hover, On;
        public bool HoverState;
        private int _last = -1;
        public void Apply()
        {
            if (Bg == null) return;
            var state = (SelectedFn?.Invoke() ?? false) ? 2 : HoverState ? 1 : 0;
            if (state == _last) return;
            Bg.color = state == 2 ? On : state == 1 ? Hover : Rest;
            // The underline shows only in the selected state (2). Set inside the state-changed branch — the bar's
            // visibility is a pure function of state, so an unchanged state already has it right (same diff as Bg).
            if (Bar != null) Bar.enabled = state == 2;
            _last = state;
        }
        public void ForceRepaint() { _last = -1; Apply(); }
    }
}
