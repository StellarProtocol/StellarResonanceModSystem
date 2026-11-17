using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// MeterRowStyle.Rounded — the opt-in house-style (Stellar Design Language) skin for the CombatMeter row. It RESKINS
// the Classic row after it is built (same GameObjects, same layout, same value bindings) instead of building a
// second row type, so every data feature (spine modes, shield bands, label styles, imagines, debuffs, voice, drag)
// keeps working unchanged. A Classic row never reaches this file: MeterRowBinding.Rounded stays null and the row
// is byte-identical to before.
//
// Cost: sprites are the shared cached BubbleBg/ring sprites (no per-row bake). Colours are recomputed only on a
// theme re-skin (ReskinActions → MarkThemeDirty) and painted only when (empty, self, dead, ring colour, window
// opacity) changes — an idle row does a delegate call + a few compares per poll, no allocation.
internal sealed partial class WindowBuilder
{
    private const int   MeterRoundTileR  = 6;     // row tile corner (SDL tile radius)
    private const int   MeterRoundBarR   = 5;     // bar track / fill corner (bar 14 px ≥ 2r+2 = 12 → no min-size jump)
    private const int   MeterRoundCellR  = 4;     // status-effect cell corner
    private const int   MeterRoundSpineR = 4;     // spine capsule sprite radius; scaled per width via pixelsPerUnitMultiplier
    private const float MeterRoundInset  = 4f;    // spine inset from the tile's top/bottom/left edge

    // Rounded skin: applies the build-time sprite/geometry changes, then hangs the live painter on the binding.
    private void ApplyRoundedMeterSkin(MeterRowBinding b, GameObject row, WindowToken token)
    {
        SetRoundSprite(b.Bg, _assets.BubbleBg(MeterRoundTileR));
        if (b.Scrim != null) SetRoundSprite(b.Scrim.GetComponent<Image>(), _assets.BubbleBg(MeterRoundTileR));
        b.SelfBorder.SetActive(false);            // square Classic borders → replaced by the rounded ring below
        b.TalkBorderGo = null!;                   // ApplyRowBorder no-ops on null; the ring draws RowBorder instead
        b.DeadMarkGo = null!;                     // dead = faded row + name strike (approved look has no skull)
        b.SpineHeightDelta = -2f * MeterRoundInset;
        b.SpineLeftExtra = MeterRoundInset - 2f;  // Classic spine sits at x=2; keep the same 4 px spine→content gap
        var spineRt = b.SpineGo.GetComponent<RectTransform>()!;
        spineRt.anchoredPosition = new Vector2(MeterRoundInset, 0f);
        var spineTrack = b.SpineGo.GetComponent<Image>()!;
        var capsule = _assets.BubbleBg(MeterRoundSpineR);
        SetRoundSprite(spineTrack, capsule); SetRoundSprite(b.SpineFill, capsule); SetRoundSprite(b.ShieldFill, capsule);

        var barGo = b.BarFillRect.parent.gameObject;
        var barTrack = barGo.GetComponent<Image>()!;
        SetRoundSprite(barTrack, _assets.BubbleBg(MeterRoundBarR));
        SetRoundSprite(b.BarFillImg, _assets.BubbleBg(MeterRoundBarR));
        SetRoundSprite(b.BarShieldImg, _assets.BubbleBg(MeterRoundBarR));

        var create = b.DebuffBlock.CreateCell!;   // cells are built lazily → round each one as it is created
        b.DebuffBlock.CreateCell = i => { var c = create(i); RoundDebuffCell(c); return c; };

        var skin = new MeterRowRoundedSkin
        {
            Assets = _assets, Opacity = _assets.OpacityProvider, Bg = b.Bg, Ring = AddRoundRing(row.transform),
            SpineTrack = spineTrack, SpineImgs = new[] { spineTrack, b.SpineFill, b.ShieldFill },
            SpineGroup = b.SpineGo.AddComponent<CanvasGroup>(), BarTrack = barTrack, BarGroup = barGo.AddComponent<CanvasGroup>(),
            BarLe = barGo.GetComponent<LayoutElement>()!, RowGroup = row.AddComponent<CanvasGroup>(),
            Rank = b.Rank, Spec = b.Spec, Hint = AddEmptyHint(token, row.transform),
        };
        token.ReskinActions.Add(skin.MarkThemeDirty);
        b.Rounded = skin;
    }

    private static void SetRoundSprite(Image? img, Sprite? sprite)
    {
        if (img == null || sprite == null) return;   // pre-bake sandbox: keep the flat rect
        img.sprite = sprite; img.type = Image.Type.Sliced;
    }

    // Self / speaking ring: a 1.5 px rounded ring over the whole tile (2× sprite drawn at multiplier 2).
    private Image AddRoundRing(Transform row)
    {
        var ring = AddStretchedImage(row, "RoundRing", Color.clear, ignoreLayout: true);
        SetRoundSprite(ring, _assets.BubbleRingHalfPx(MeterRoundTileR, 3));
        ring.pixelsPerUnitMultiplier = 2f;
        ring.gameObject.SetActive(false);
        return ring;
    }

    // Right-aligned muted hint for an empty slot (MeterRowData.EmptyHint). Ignore-layout overlay, built inactive.
    private Text AddEmptyHint(WindowToken token, Transform row)
    {
        var go = UGuiPrimitives.NewChild("EmptyHint", row);
        go.AddComponent<LayoutElement>().ignoreLayout = true;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(12f, 0f); rt.offsetMax = new Vector2(-10f, 0f);
        var txt = go.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(txt, Scaled(11), TextAnchor.MiddleRight, bold: false);
        ApplyMenuFont(txt); txt.alignByGeometry = true; txt.horizontalOverflow = HorizontalWrapMode.Overflow;
        RegisterTextSizeReskin(token, txt, 11);
        go.SetActive(false);
        return txt;
    }

    // Status-effect cell → radius-4 tile: the kind-coloured Frame becomes the rounded ring (1.5 px showing around a
    // rounded dark inset); the icon and foot bar move 1 px inside the inset so their square corners stay within it.
    private void RoundDebuffCell(DebuffCell c)
    {
        SetRoundSprite(c.Frame, _assets.BubbleBg(MeterRoundCellR));
        var inset = c.InsetGo.GetComponent<Image>();
        SetRoundSprite(inset, _assets.BubbleBg(MeterRoundCellR - 1));
        var irt = c.InsetGo.GetComponent<RectTransform>()!;
        irt.offsetMin = new Vector2(1.5f, 1.5f); irt.offsetMax = new Vector2(-1.5f, -1.5f);
        var art = c.Art.rectTransform;
        art.offsetMin = new Vector2(1f, 1f); art.offsetMax = new Vector2(-1f, -1f);
        c.FillRt.anchoredPosition = new Vector2(1f, 1f); c.FillRt.sizeDelta = new Vector2(-2f, 3f);
    }

    // Live painter for one rounded row. Theme colours are composed OPAQUE (SDL §3 tokens) on a re-skin, then each
    // fill takes its alpha × the live window opacity (SDL §5) at paint time.
    internal sealed class MeterRowRoundedSkin
    {
        public WindowThemeAssets Assets = null!;
        public Func<float>? Opacity;
        public Image Bg = null!, Ring = null!, SpineTrack = null!, BarTrack = null!;
        public Image?[] SpineImgs = null!;
        public CanvasGroup SpineGroup = null!, BarGroup = null!, RowGroup = null!;
        public LayoutElement BarLe = null!;
        public Text Rank = null!, Spec = null!, Hint = null!;

        private const float TileAlpha = 0.92f, SelfAlpha = 0.95f, TrackAlpha = 0.90f, EmptyAlpha = 0.75f;
        private const float DeadAlpha = 0.55f, SelfWash = 0.20f, SelfRingAlpha = 0.75f;

        private Color _tile, _self, _recessed, _spineTrack, _accent, _muted;
        /// <summary>The faint recessed label (rank + name) of an empty slot — the binding reads it for the name.</summary>
        public Color EmptyLabel { get; private set; }
        private bool _themeDirty = true;
        private int _lastKey = -1;
        private float _lastOpacity = -1f, _lastSpineW = -1f;
        private ColorRgba _lastRing = new(-2f, -2f, -2f, -2f);
        private string? _lastHint;

        public void MarkThemeDirty() => _themeDirty = true;

        public void Apply(in MeterRowData d)
        {
            if (_themeDirty) Recompute();
            float op = Mathf.Clamp01(Opacity?.Invoke() ?? 1f);
            int key = (d.IsEmptySlot ? 1 : 0) | (d.IsSelf ? 2 : 0) | (d.Dead ? 4 : 0);
            if (key != _lastKey || !Mathf.Approximately(op, _lastOpacity) || !d.RowBorder.Equals(_lastRing))
            {
                Paint(d, op);
                _lastKey = key; _lastOpacity = op; _lastRing = d.RowBorder;
            }
            ApplySpineShape(d);
            var hint = d.IsEmptySlot && !string.IsNullOrEmpty(d.EmptyHint) ? d.EmptyHint : null;
            if (!ReferenceEquals(hint, _lastHint) && hint != _lastHint)
            {
                Hint.gameObject.SetActive(hint != null);
                if (hint != null) Hint.text = hint;
                _lastHint = hint;
            }
        }

        private void Paint(in MeterRowData d, float op)
        {
            bool empty = d.IsEmptySlot;
            Bg.color = empty ? Fade(_recessed, EmptyAlpha, op) : d.IsSelf ? Fade(_self, SelfAlpha, op) : Fade(_tile, TileAlpha, op);
            SpineTrack.color = Fade(_spineTrack, 1f, op);
            BarTrack.color = Fade(_recessed, TrackAlpha, op);
            SpineGroup.alpha = empty ? 0f : 1f;
            BarGroup.alpha = empty ? 0f : 1f;
            if (BarLe.ignoreLayout != empty) BarLe.ignoreLayout = empty;   // empty slot = one centred line
            RowGroup.alpha = d.Dead && !empty ? DeadAlpha : 1f;
            Rank.color = empty ? EmptyLabel : _muted;
            // Ring: a plugin RowBorder (e.g. green while speaking) wins over the self ring.
            bool speak = d.RowBorder.A > 0f && !empty;
            bool ring = speak || (d.IsSelf && !empty);
            Ring.gameObject.SetActive(ring);
            if (ring)
                Ring.color = speak ? new Color(d.RowBorder.R, d.RowBorder.G, d.RowBorder.B, d.RowBorder.A * op)
                                   : Fade(_accent, SelfRingAlpha, op);
        }

        // Spine capsule: the 9-slice border (r+1) is scaled so each end is exactly width/2 → round ends at every
        // spine width (3/5/8 px), the same trick as CapsuleGeometry.CircleMultiplier. Re-applied on a width change.
        private void ApplySpineShape(in MeterRowData d)
        {
            var w = d.SpineWidth > 0f ? d.SpineWidth : MeterSpineW;
            if (Mathf.Approximately(w, _lastSpineW)) return;
            _lastSpineW = w;
            var mul = (MeterRoundSpineR + 1f) / Mathf.Max(0.5f, w * 0.5f);
            foreach (var img in SpineImgs) if (img != null) img.pixelsPerUnitMultiplier = mul;
        }

        private void Recompute()
        {
            _themeDirty = false;
            _lastKey = -1;   // force a repaint with the new colours
            var bg = Opaque(Assets.MenuBackground);
            var text = Opaque(Assets.MenuText);
            _accent = Opaque(Assets.MenuAccent);
            _muted = Opaque(Assets.MenuMuted);
            var panel = Over(new Color(0f, 0f, 0f, 0.20f), bg);
            _tile = Over(WithA(_accent, 0.06f), Over(WithA(text, 0.08f), panel));
            _self = Over(WithA(_accent, SelfWash), _tile);
            _recessed = Over(new Color(0f, 0f, 0f, 0.28f), panel);
            _spineTrack = Over(WithA(text, 0.06f), _tile);
            EmptyLabel = WithA(text, 0.32f);
            Spec.color = _muted;
            Hint.color = _muted;
        }

        private static Color Opaque(Color c) => new(c.r, c.g, c.b, 1f);
        private static Color WithA(Color c, float a) => new(c.r, c.g, c.b, a);
        private static Color Fade(Color c, float a, float op) => new(c.r, c.g, c.b, a * op);
        private static Color Over(Color fg, Color bg)
            => new(bg.r + (fg.r - bg.r) * fg.a, bg.g + (fg.g - bg.g) * fg.a, bg.b + (fg.b - bg.b) * fg.a, 1f);
    }
}
