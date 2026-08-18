using System;
using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Binding inner-classes for WindowBuilder — captured at build, re-pulled by WindowToken.Apply() (poll-diff).
// Split out of WindowBuilder.cs to keep that file under the size gate. Each binding holds the built uGUI
// component(s) + the dynamic Func, and diffs against its last value so a no-change poll does no work.
// (The bespoke CombatMeter MeterRowBinding lives in the sibling partial WindowBuilder.MeterRowBinding.cs.)
internal sealed partial class WindowBuilder
{
    internal sealed class SliderBinding
    {
        public Slider S = null!;
        public Func<float> Get = null!;
        public Func<bool>? EnabledFn;
        private bool _init; private float _last;
        public void Apply()
        {
            if (S == null || !S.gameObject.activeInHierarchy) return;
            var v = Get();
            if (!_init || !Mathf.Approximately(v, _last)) { S.SetValueWithoutNotify(v); _last = v; _init = true; }
            if (EnabledFn != null) S.interactable = EnabledFn();
        }
    }

    internal sealed class TextBinding
    {
        public Text C = null!;
        // HudOverlay (SurfaceStyle.HudOverlay) only: the offset-twin drop-shadow Text (UnityEngine.UI.Shadow is
        // stripped from interop, so the HUD look uses a manual twin). Null on the Menu path → behaviour unchanged.
        public Text? Shadow;
        public Func<string> TextFn = null!;
        public Func<ColorRgba?>? ColorFn;
        // HudOverlay only: live font size (TextElement.DynamicFontSize, e.g. ScreenHeight/19), re-pulled per apply
        // and applied to BOTH the foreground and the shadow twin. Null on the Menu path → the size stays fixed.
        public Func<int>? DynamicFontSizeFn;
        public float EllipsizeWidth;   // >0: single-line, truncated with "..." to fit this width (no spill/wrap)
        // Emphasised (bold) text re-derives its weight from the CURRENT string each time the text changes:
        // real bold for Latin, regular weight for complex scripts (CJK/kana/Hangul/Thai) whose glyphs Unity's
        // synthetic bold mangles (i18n P0 JA/TH bold-header bug). Re-checked on live language switches and any
        // dynamic-content change. Non-emphasis bindings leave fontStyle untouched.
        public bool Emphasis;
        private string? _last;
        private int _lastFontSize;
        public void Apply()
        {
            // Skip hidden rows (Conditional/List SetActive=false) — avoids evaluating TextFn (which formats
            // labels/hex strings) for the dozens of off-screen slot rows in the theme editor every poll.
            if (C == null || !C.gameObject.activeInHierarchy) return;
            // Live font size (HudOverlay) — mirrors HudElementBuilder.TextBinding: diff, then push to fg + twin.
            if (DynamicFontSizeFn != null)
            {
                var fs = Math.Max(1, DynamicFontSizeFn());
                if (fs != _lastFontSize) { C.fontSize = fs; if (Shadow != null) Shadow.fontSize = fs; _lastFontSize = fs; }
            }
            var s = TextFn();
            if (s != _last)
            {
                _last = s;
                var text = EllipsizeWidth > 0f ? UGuiPrimitives.Ellipsize(C, s, EllipsizeWidth) : s;
                C.text = text;
                if (Shadow != null) Shadow.text = text;   // twin mirrors the foreground string (HudOverlay)
                // Re-derive emphasis weight from the new string so complex scripts drop faux-bold (see Emphasis).
                if (Emphasis)
                {
                    var style = UGuiPrimitives.EmphasisStyle(true, s);
                    C.fontStyle = style;
                    if (Shadow != null) Shadow.fontStyle = style;
                }
            }
            if (ColorFn != null && ColorFn() is { } v)
            {
                C.color = new Color(v.R, v.G, v.B, v.A);
                // Keep the shadow's dark rgb, track only the foreground alpha — matches HudElementBuilder's twin.
                if (Shadow != null) { var sc = Shadow.color; Shadow.color = new Color(sc.r, sc.g, sc.b, v.A); }
            }
        }
    }

    internal sealed class ButtonBinding
    {
        public Button B = null!;
        public Text Label = null!;
        public Func<string> LabelFn = null!;
        public Func<bool>? EnabledFn;
        public Image? Img; public Sprite? Normal; public Sprite? Accent; public Func<bool>? ActiveFn;
        private string? _last;
        private bool _activeInit, _lastActive;
        // Re-skin: re-assign the (style/theme-updated) sprite directly; reset the active-diff so Apply re-runs.
        public void Resprite()
        {
            if (Img == null) return;
            Img.sprite = (ActiveFn?.Invoke() ?? false) ? Accent : Normal;
            _activeInit = false;
        }
        public void Apply()
        {
            if (B == null || !B.gameObject.activeInHierarchy) return;
            var s = LabelFn();
            if (s != _last) { Label.text = s; _last = s; }
            if (EnabledFn != null) B.interactable = EnabledFn();
            if (ActiveFn != null && Img != null)
            {
                var on = ActiveFn();
                if (!_activeInit || on != _lastActive) { Img.sprite = on ? Accent : Normal; _lastActive = on; _activeInit = true; }
            }
        }
    }

    internal sealed class ToggleBinding
    {
        public Image Track = null!;
        public Button Btn = null!;
        public RectTransform Knob = null!;
        public Func<bool> Get = null!;
        public Func<bool>? EnabledFn;
        public Color On, Off;
        private bool _init, _last;
        private bool _enabledInit, _lastEnabled;
        public void Apply()
        {
            if (Track == null || !Track.gameObject.activeInHierarchy) return;
            var on = Get();
            if (_init && on == _last) { ApplyEnabled(); return; }
            Track.color = on ? On : Off;
            Knob.anchorMin = Knob.anchorMax = Knob.pivot = new Vector2(on ? 1f : 0f, 0.5f);
            Knob.anchoredPosition = new Vector2(on ? -2f : 2f, 0f);
            _last = on; _init = true;
            ApplyEnabled();
        }

        private void ApplyEnabled()
        {
            if (EnabledFn == null) return;
            var enabled = EnabledFn();
            if (_enabledInit && enabled == _lastEnabled) return;
            if (Btn != null) Btn.interactable = enabled;
            _lastEnabled = enabled; _enabledInit = true;
        }
    }

    internal sealed class SwatchBinding
    {
        public Image Img = null!;
        public Func<ColorRgba> Get = null!;
        private bool _init; private ColorRgba _last;
        public void Apply()
        {
            if (Img == null || !Img.gameObject.activeInHierarchy) return;
            var c = Get();
            if (_init && c.Equals(_last)) return;
            Img.color = new Color(c.R, c.G, c.B, c.A); _last = c; _init = true;
        }
    }

    internal sealed class BarBinding
    {
        // Width via the clip rect's right anchor, NOT fillAmount (spriteless Image ignores it) — see BuildBarTrack.
        // For a HudOverlay Default bar FillRect is left null (the fill is an Image.Type.Filled smoothed by a pulse,
        // not an anchor-clip) so this binding then carries only the label + its shadow twin.
        public RectTransform FillRect = null!;
        public Func<float> Fraction = null!;
        // Label + (HudOverlay only) its offset-twin shadow. LabelShadow null on the Menu path → unchanged.
        public Text? Label; public Text? LabelShadow; public Func<string>? LabelFn;
        private float _lastFrac = -1f; private string? _lastLabel;
        public void Apply()
        {
            if (FillRect != null && !FillRect.gameObject.activeInHierarchy) return;
            if (FillRect != null)
            {
                var f = Mathf.Clamp01(Fraction());
                if (!Mathf.Approximately(f, _lastFrac)) { FillRect.anchorMax = new Vector2(f, 1f); _lastFrac = f; }
            }
            if (Label != null && LabelFn != null)
            {
                var s = LabelFn();
                if (s != _lastLabel) { Label.text = s; if (LabelShadow != null) LabelShadow.text = s; _lastLabel = s; }
            }
        }
    }

    // Live window-body opacity — sets the frame Image's alpha each poll from IChromeStyle.WindowOpacity, so
    // the opacity slider updates in real time WITHOUT rebaking the sprite or rebuilding the canvas (the flicker).
    internal sealed class FrameOpacityBinding
    {
        public Image Img = null!;
        public Func<float> Opacity = null!;
        private float _last = -1f;
        public void Apply()
        {
            if (Img == null) return;
            // ChromeKill hides the frame entirely. Set each apply (cheap/idempotent) and BEFORE the alpha
            // short-circuit, so toggling it always takes effect even when the alpha is unchanged.
            Img.enabled = !PerfControls.ChromeKill;
            var a = PerfControls.ForceOpaque ? 1f : Opacity();
            if (Mathf.Approximately(a, _last)) return;
            var c = Img.color; Img.color = new Color(c.r, c.g, c.b, a); _last = a;
        }
    }

    internal sealed class CondBinding
    {
        public Func<bool> When = null!;
        public GameObject Then = null!;
        public GameObject? Else;
        private bool _init, _last;
        public bool Apply()   // returns true when the active branch changed (→ caller forces a layout rebuild)
        {
            var b = When();
            if (_init && b == _last) return false;
            if (Then != null) Then.SetActive(b);
            if (Else != null) Else.SetActive(!b);
            _last = b; _init = true;
            return true;
        }
    }

    // Poll-diffed dynamic atlas sub-rect: re-pulls UvFunc and re-sets RawImage.uvRect only when it changes, so a
    // recycled SpriteElement slot tracks its backing data's icon (mirrors MeterRowBinding's crest uvRect rebind).
    internal sealed class SpriteBinding
    {
        public RawImage Raw = null!;
        public Func<UvRect> Uv = null!;
        private UvRect _last;
        private bool _init;
        public void Apply()
        {
            if (Raw == null || !Raw.gameObject.activeInHierarchy) return;
            var u = Uv();
            if (_init && u.Equals(_last)) return;
            Raw.uvRect = new UnityEngine.Rect(u.X, u.Y, u.W, u.H);
            _last = u; _init = true;
        }
    }

    internal sealed class ListBinding
    {
        public Func<int> Count = null!;
        public GameObject[] Slots = System.Array.Empty<GameObject>();
        private int _last = -1;
        public bool Apply()   // returns true when the visible count changed (→ caller forces a layout rebuild)
        {
            var n = Count();
            if (n == _last) return false;
            for (var i = 0; i < Slots.Length; i++) if (Slots[i] != null) Slots[i].SetActive(i < n);
            _last = n;
            return true;
        }
    }
}
