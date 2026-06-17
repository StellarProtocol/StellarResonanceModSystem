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
        public Func<string> TextFn = null!;
        public Func<ColorRgba?>? ColorFn;
        public float EllipsizeWidth;   // >0: single-line, truncated with "..." to fit this width (no spill/wrap)
        private string? _last;
        public void Apply()
        {
            // Skip hidden rows (Conditional/List SetActive=false) — avoids evaluating TextFn (which formats
            // labels/hex strings) for the dozens of off-screen slot rows in the theme editor every poll.
            if (C == null || !C.gameObject.activeInHierarchy) return;
            var s = TextFn();
            if (s != _last) { _last = s; C.text = EllipsizeWidth > 0f ? UGuiPrimitives.Ellipsize(C, s, EllipsizeWidth) : s; }
            if (ColorFn != null && ColorFn() is { } v) C.color = new Color(v.R, v.G, v.B, v.A);
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
        public RectTransform Knob = null!;
        public Func<bool> Get = null!;
        public Color On, Off;
        private bool _init, _last;
        public void Apply()
        {
            if (Track == null || !Track.gameObject.activeInHierarchy) return;
            var on = Get();
            if (_init && on == _last) return;
            Track.color = on ? On : Off;
            Knob.anchorMin = Knob.anchorMax = Knob.pivot = new Vector2(on ? 1f : 0f, 0.5f);
            Knob.anchoredPosition = new Vector2(on ? -2f : 2f, 0f);
            _last = on; _init = true;
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
        public RectTransform FillRect = null!;
        public Func<float> Fraction = null!;
        public Text? Label; public Func<string>? LabelFn;
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
                if (s != _lastLabel) { Label.text = s; _lastLabel = s; }
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

    // Poll-diffed binding for one bespoke CombatMeter row. Re-pulls MeterRowData on the window refresh tick and
    // updates the bg/self-highlight, HP spine fill, crest (lazy atlas upload), name·spec·share texts, role bar
    // fill, per-second/total overlay, and offline scrim. Diffs the cheap scalar/string fields; structural
    // SetActive toggles are idempotent so re-applying is harmless.
    internal sealed class MeterRowBinding
    {
        public Func<MeterRowData> Data = null!;
        public VerticalLayoutGroup ContentVlg = null!;   // bottom padding trimmed when imagine size is Large
        public Image Bg = null!;
        public GameObject SelfBorder = null!;
        public Image SpineFill = null!;
        public GameObject SpineGo = null!;          // HP spine cell (toggled by ShowHpBar)
        public RawImage Crest = null!;
        public GameObject CrestCellGo = null!;      // 22px crest layout cell (toggled by ShowCrest)
        public Text Rank = null!;
        public GameObject RankGo = null!;           // rank label (toggled by ShowRank)
        public Text Name = null!;
        public GameObject DeadMarkGo = null!;       // drawn skull (toggled by Dead)
        public GameObject NameStrikeGo = null!;     // strike line over the name (toggled by Dead)
        public Text ClassName = null!;              // optional base-class line (toggled by ShowClassName)
        public GameObject ClassNameGo = null!;
        public Text Spec = null!;
        public GameObject SpecGo = null!;
        public Text Score = null!;                  // ability-score pill text (toggled by ShowAbilityScore)
        public GameObject ScoreGo = null!;
        public Text Share = null!;
        public GameObject ShareGo = null!;
        public GameObject LeaderGo = null!;
        public RectTransform BarFillRect = null!;   // width-clipped fill container (anchorMax.x = fraction)
        public Image BarFillImg = null!;            // role-colour fill inside the clip
        public Text Primary = null!;
        public GameObject PrimaryGo = null!;        // per-second overlay (toggled by ShowPrimary)
        public Text Secondary = null!;
        public GameObject SecondaryGo = null!;
        public GameObject Scrim = null!;
        public ImagineCell Imagine0Cell = null!;   // trailing Battle-Imagine cells (left=X slot, right=Z slot)
        public ImagineCell Imagine1Cell = null!;
        public Transform ImagineGroup = null!;     // the re-parentable imagine cluster (for ImaginePosition)
        public Transform TopLine = null!;          // top-line HLG host (top-right / left positions)
        public Transform RightColHost = null!;     // right-column host (RightColumn position)

        private object? _lastAtlas;
        private bool _atlasResolved;
        // Poll-diff caches — an idle row (unchanged values) writes nothing, avoiding redundant Text mesh
        // rebuilds / Image vertex-dirty rebatches every poll.
        private bool _selfInit, _lastSelf;
        private int _lastLeader = -1;
        private float _lastHp = -1f, _lastBar = -1f;
        private ColorRgba _lastHpCol, _lastRoleCol;
        private string? _lastRank, _lastName, _lastSpec, _lastShare, _lastPrimary, _lastSecondary;
        private int _lastSpecVis = -1, _lastShareVis = -1, _lastSecondaryVis = -1, _lastOffline = -1;
        private int _lastRankVis = -1, _lastCrestVis = -1, _lastSpineVis = -1, _lastPrimaryVis = -1;
        private int _lastClassVis = -1, _lastScoreVis = -1, _lastDead = -1, _lastImgLayout = -1;
        private string? _lastClass, _lastScore;
        private static readonly ColorRgba MeterDeadBarRgba = new(0.35f, 0.27f, 0.27f, 1f);  // greyed bar when dead
        private ImagineCellCache _img0, _img1;

        public void Apply()
        {
            if (Bg == null || !Bg.gameObject.activeInHierarchy) return;
            var d = Data();

            if (!_selfInit || d.IsSelf != _lastSelf)
            {
                Bg.color = d.IsSelf ? MeterSelfBg : MeterRowBg;
                if (SelfBorder != null) SelfBorder.SetActive(d.IsSelf);
                _lastSelf = d.IsSelf; _selfInit = true;
            }

            if (SpineFill != null)
            {
                var hp = Mathf.Clamp01(d.HpFraction);   // anchorMax.y = HP fraction (bottom-anchored); see BuildMeterBackplate
                if (!Mathf.Approximately(hp, _lastHp)) { SpineFill.rectTransform.anchorMax = new Vector2(1f, hp); _lastHp = hp; }
                if (!d.HpColor.Equals(_lastHpCol)) { SpineFill.color = ToColor(d.HpColor); _lastHpCol = d.HpColor; }
            }

            ApplyCrest(d);
            ApplyVisibility(d);

            var rank = d.Rank ?? "";
            if (rank != _lastRank) { Rank.text = rank; _lastRank = rank; }

            var name = d.Name ?? "";
            if (name != _lastName) { Name.text = name; _lastName = name; }

            var showSpec = d.ShowSpec && !string.IsNullOrEmpty(d.Spec);
            if (_lastSpecVis != (showSpec ? 1 : 0)) { SpecGo.SetActive(showSpec); _lastSpecVis = showSpec ? 1 : 0; }
            if (showSpec) { var s = "· " + d.Spec; if (s != _lastSpec) { Spec.text = s; _lastSpec = s; } }

            var showShare = d.ShowShare && !string.IsNullOrEmpty(d.SharePercent);
            if (_lastShareVis != (showShare ? 1 : 0)) { ShareGo.SetActive(showShare); _lastShareVis = showShare ? 1 : 0; }
            if (showShare && d.SharePercent != _lastShare) { Share.text = d.SharePercent; _lastShare = d.SharePercent; }

            var bar = Mathf.Clamp01(d.BarFraction);
            if (!Mathf.Approximately(bar, _lastBar)) { BarFillRect.anchorMax = new Vector2(bar, 1f); _lastBar = bar; }
            { var rc = d.Dead ? MeterDeadBarRgba : d.RoleColor; if (!rc.Equals(_lastRoleCol)) { BarFillImg.color = ToColor(rc); _lastRoleCol = rc; } }

            var primary = d.PrimaryValue ?? "";
            if (primary != _lastPrimary) { Primary.text = primary; _lastPrimary = primary; }

            var showSecondary = d.ShowSecondary && !string.IsNullOrEmpty(d.SecondaryValue);
            if (_lastSecondaryVis != (showSecondary ? 1 : 0)) { SecondaryGo.SetActive(showSecondary); _lastSecondaryVis = showSecondary ? 1 : 0; }
            if (showSecondary && d.SecondaryValue != _lastSecondary) { Secondary.text = d.SecondaryValue; _lastSecondary = d.SecondaryValue; }

            if (Scrim != null && _lastOffline != (d.Offline ? 1 : 0)) { Scrim.SetActive(d.Offline); _lastOffline = d.Offline ? 1 : 0; }
            ApplyImagineLayout(d);
            ApplyImagines(d);
        }

        // Poll-diff the per-element visibility toggles + leader flag (kept out of Apply to respect the method-LoC
        // cap). Each is an idempotent SetActive gated on a cached 0/1 so an unchanged poll writes nothing.
        private void ApplyVisibility(in MeterRowData d)
        {
            if (RankGo != null && _lastRankVis != (d.ShowRank ? 1 : 0)) { RankGo.SetActive(d.ShowRank); _lastRankVis = d.ShowRank ? 1 : 0; }
            if (CrestCellGo != null && _lastCrestVis != (d.ShowCrest ? 1 : 0)) { CrestCellGo.SetActive(d.ShowCrest); _lastCrestVis = d.ShowCrest ? 1 : 0; }
            if (SpineGo != null && _lastSpineVis != (d.ShowHpBar ? 1 : 0)) { SpineGo.SetActive(d.ShowHpBar); _lastSpineVis = d.ShowHpBar ? 1 : 0; }
            if (PrimaryGo != null && _lastPrimaryVis != (d.ShowPrimary ? 1 : 0)) { PrimaryGo.SetActive(d.ShowPrimary); _lastPrimaryVis = d.ShowPrimary ? 1 : 0; }
            var showLeader = d.IsLeader && d.ShowLeaderFlag;
            if (LeaderGo != null && _lastLeader != (showLeader ? 1 : 0)) { LeaderGo.SetActive(showLeader); _lastLeader = showLeader ? 1 : 0; }

            var showClass = d.ShowClassName && !string.IsNullOrEmpty(d.ClassName);
            if (_lastClassVis != (showClass ? 1 : 0)) { ClassNameGo.SetActive(showClass); _lastClassVis = showClass ? 1 : 0; }
            if (showClass && d.ClassName != _lastClass) { ClassName.text = d.ClassName; _lastClass = d.ClassName; }

            var showScore = d.ShowAbilityScore && !string.IsNullOrEmpty(d.AbilityScore);
            if (_lastScoreVis != (showScore ? 1 : 0)) { ScoreGo.SetActive(showScore); _lastScoreVis = showScore ? 1 : 0; }
            if (showScore && d.AbilityScore != _lastScore) { Score.text = d.AbilityScore; _lastScore = d.AbilityScore; }

            if (_lastDead != (d.Dead ? 1 : 0))
            {
                if (DeadMarkGo != null) DeadMarkGo.SetActive(d.Dead);
                if (NameStrikeGo != null) NameStrikeGo.SetActive(d.Dead);
                Name.color = d.Dead ? MeterDeadName : MeterNameCol;
                _lastDead = d.Dead ? 1 : 0;
            }

        }

        // Re-parent the imagine cluster for MeterRowData.ImaginePosition + size-driven content padding. Keyed
        // on (size,position) so it only relays out on change. Bottom padding trimmed for Large (no bar squeeze);
        // right padding reserved when the cluster sits in the right column (so bar/text don't run under it).
        private void ApplyImagineLayout(in MeterRowData d)
        {
            int key = (int)d.ImagineSize * 4 + (int)d.ImaginePosition;
            if (_lastImgLayout == key) return;
            _lastImgLayout = key;
            if (ImagineGroup != null && TopLine != null && RightColHost != null)
            {
                switch (d.ImaginePosition)
                {
                    case ImaginePosition.Left:        ImagineGroup.SetParent(TopLine, false); ImagineGroup.SetSiblingIndex(0); break;
                    case ImaginePosition.RightColumn: ImagineGroup.SetParent(RightColHost, false); break;
                    default:                          ImagineGroup.SetParent(TopLine, false); ImagineGroup.SetSiblingIndex(TopLine.childCount - 1); break;
                }
            }
            if (ContentVlg != null)
            {
                var p = ContentVlg.padding;
                int bottom = d.ImagineSize == ImagineSize.Large ? 1 : 5;
                int right = d.ImaginePosition == ImaginePosition.RightColumn ? 58 + (int)MeterPad : (int)MeterPad;
                ContentVlg.padding = new RectOffset(p.left, right, p.top, bottom);
            }
        }

        // Poll-diff the two trailing Imagine cells (kept out of Apply to respect the method-LoC cap).
        private void ApplyImagines(in MeterRowData d)
        {
            var opts = new ImagineOpts(d.ShowImagine, d.ShowImagineCooldown, d.ImagineSize);
            BindImagineCell(Imagine0Cell, d.Imagine0, opts, ref _img0);
            BindImagineCell(Imagine1Cell, d.Imagine1, opts, ref _img1);
        }

        private static Color ToColor(ColorRgba c) => new(c.R, c.G, c.B, c.A);

        // Fit a (srcW×srcH) source into a square box, preserving aspect (centre-letterboxed). Mirrors
        // MeterRowView.AspectFit — the crest image is centre-anchored so the returned size centres in the box.
        private static Vector2 AspectFit(float box, float srcW, float srcH)
        {
            if (srcW <= 0f || srcH <= 0f || Mathf.Approximately(srcW, srcH)) return new Vector2(box, box);
            float aspect = srcW / srcH;
            return aspect > 1f ? new Vector2(box, box / aspect) : new Vector2(box * aspect, box);
        }

        // Crest texture loads async (class icons arrive after the meter is built); the plugin passes the game's
        // atlas Texture as an opaque object handle. Re-bind only when the handle changes (it's stable once loaded).
        private void ApplyCrest(in MeterRowData d)
        {
            if (Crest == null) return;
            if (!_atlasResolved || !ReferenceEquals(d.CrestTexture, _lastAtlas))
            {
                _lastAtlas = d.CrestTexture;
                _atlasResolved = true;
                var tex = d.CrestTexture as Texture; // MeterRowData.CrestTexture contract: MUST be a UnityEngine.Texture2D; non-Texture2D silently renders nothing
                Crest.texture = tex;
                Crest.enabled = tex != null;
                // Letterbox a non-square atlas cell inside the 22×22 box (AspectFit) instead of stretching it.
                if (tex != null) Crest.rectTransform.sizeDelta = AspectFit(22f, d.CrestUv.W * tex.width, d.CrestUv.H * tex.height);
            }
            Crest.uvRect = new UnityEngine.Rect(d.CrestUv.X, d.CrestUv.Y, d.CrestUv.W, d.CrestUv.H);
        }
    }

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
