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

    // Per-apply re-tint for a SelectableElement row. Diffs on a 0/1/2 state (rest/hover/selected) like the
    // other bindings, so a no-change poll writes nothing — avoids marking the bg Image vertex-dirty (a
    // redundant canvas rebatch) every Apply for every idle row. HoverState is pushed by the ticker hover hook;
    // Selected() is polled. ForceRepaint() (resets the diff) is used by the hover hook, the initial paint, and
    // the theme-reskin (which recomputes Rest/Hover/On).
    internal sealed class SelectableBinding
    {
        public Image Bg = null!;
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
            _last = state;
        }
        public void ForceRepaint() { _last = -1; Apply(); }
    }
}
