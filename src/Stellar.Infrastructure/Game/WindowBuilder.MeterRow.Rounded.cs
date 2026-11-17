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
    private void ApplyRoundedMeterSkin(MeterRowBinding b, GameObject row, WindowToken token, Func<float>? backing)
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
        StripRectangularBarOverlays(b);
        ShareTopLineBaseline(b);
        var rankSlot = FixRankSlot(b, token);

        var create = b.DebuffBlock.CreateCell!;   // cells are built lazily → round each one as it is created
        b.DebuffBlock.CreateCell = i => { var c = create(i); RoundDebuffCell(c); return c; };

        var skin = new MeterRowRoundedSkin
        {
            Assets = _assets, Opacity = _assets.OpacityProvider, Backing = backing, Bg = b.Bg, Ring = AddRoundRing(row.transform),
            SpineTrack = spineTrack, SpineImgs = new[] { spineTrack, b.SpineFill, b.ShieldFill },
            SpineGroup = b.SpineGo.AddComponent<CanvasGroup>(), BarTrack = barTrack, BarGroup = barGo.AddComponent<CanvasGroup>(),
            BarLe = barGo.GetComponent<LayoutElement>()!, RowGroup = row.AddComponent<CanvasGroup>(),
            Rank = b.Rank, RankSlot = rankSlot, Spec = b.Spec, Hint = AddEmptyHint(token, row.transform),
        };
        AttachTopLineFit(skin, b);
        token.ReskinActions.Add(skin.MarkThemeDirty);
        b.Rounded = skin;
    }

    // The Classic bar's overlays are RECTANGLES and can't follow a rounded fill:
    //  • FillClip's RectMask2D is disabled for good. RectMask2D is a hard per-fragment clip (UnityGet2DClipping =
    //    step) at the fill's fractional edge: it cut away the fill's own anti-aliased top/left edge pixels and let
    //    the dark track's edge show through → a dark 1 px rim that made the rounded fill read as a square box
    //    (in-game 2026-11-17). The sheen is re-clipped to the ROUNDED shape instead (ClipSheenToRoundedFill).
    //  • Shadow label fades (64 px dark ramps anchored at the bar ends — square over the rounded track ends).
    //    Nulled refs → ApplyLabelStyle renders MeterLabelStyle.Shadow as the text Outline halo instead, which is
    //    also the mockup's look (text-shadow glow on the values).
    private void StripRectangularBarOverlays(MeterRowBinding b)
    {
        if (b.FillMask != null) b.FillMask.enabled = false;
        b.FillMask = null;   // HideSheen must never re-enable it
        if (b.SheenGo != null) b.SheenGo = ClipSheenToRoundedFill(b.SheenGo);
        if (b.PrimaryFadeImg != null) b.PrimaryFadeImg.gameObject.SetActive(false);
        if (b.SecondaryFadeImg != null) b.SecondaryFadeImg.gameObject.SetActive(false);
        b.PrimaryFadeImg = null; b.SecondaryFadeImg = null;
    }

    // Sheen clipped to the rounded FILL (user 2026-11-17: "shine clipped, but add an on/off setting"). The band moves
    // under a stencil Mask whose graphic is the bar's rounded sprite, sized to FillClip (= the fill width), so the
    // shine stays inside the rounded fill and tracks it as it grows. The FILL is a SIBLING of the mask, not a child,
    // so its own soft edges are never clipped (the bug RectMask2D caused). Hierarchy under FillClip:
    //   Fill (rounded r5) · SheenMask [Image r4 + Mask(showMaskGraphic=false) + Canvas] > Sheen (RawImage)
    // • Inset 1 px, radius 4 (concentric with the r5 fill): the stencil write alpha-clips at α>0.001, so a mask of
    //   the fill's exact shape would include its faint AA fringe and lay full-strength shine over it; one px in, the
    //   shine edge sits on solid fill and the band's own fade hides the 1 px it doesn't reach.
    // • The batching Canvas moves from the band UP to the mask container: the stencil push (mask graphic), the band
    //   and the stencil pop (Mask's unmask pass) then all draw inside ONE canvas, so their order can't be split across
    //   canvas batches, and the per-frame sweep still re-batches only this tiny canvas, not the row/window. Stencil
    //   depth counts Masks up to the nearest overrideSorting canvas — this one is overrideSorting=false, so a Mask
    //   above the window (none today) would still nest correctly. The list viewport's RectMask2D also still clips it.
    // • Cost: one Mask per row = +2 draw calls (stencil write + unmask) on top of the canvas break the band already
    //   had; stencil materials are shared via StencilMaterial (no per-row material). No per-frame work is added — the
    //   SheenDriver is unchanged. The binding's HideSheen now toggles the WHOLE container (mask + band), so a
    //   hidden sheen costs no mask draws at all.
    private GameObject ClipSheenToRoundedFill(GameObject sheenGo)
    {
        var clip = sheenGo.transform.parent;
        var maskGo = UGuiPrimitives.NewChild("SheenMask", clip);
        maskGo.transform.SetSiblingIndex(sheenGo.transform.GetSiblingIndex());   // draw where the band drew (over the fill)
        var rt = maskGo.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0f, 0.5f);
        rt.offsetMin = new Vector2(1f, 1f); rt.offsetMax = new Vector2(-1f, -1f);
        var img = maskGo.AddComponent<Image>(); img.color = Color.white; img.raycastTarget = false;
        SetRoundSprite(img, _assets.BubbleBg(MeterRoundBarR - 1));   // null pre-bake → rectangular stencil, still clipped
        maskGo.AddComponent<Mask>().showMaskGraphic = false;
        maskGo.AddComponent<Canvas>();   // batching boundary only — overrideSorting left false (see BuildMeterSheen)
        var inner = sheenGo.GetComponent<Canvas>();
        if (inner != null) UnityEngine.Object.Destroy(inner);   // one canvas for push/band/pop (see above)
        sheenGo.transform.SetParent(maskGo.transform, false);   // band anchors (left, full height) now follow the mask
        return maskGo;
    }

    // Top-line labels must share a baseline (mockup `.mr .t`: one flex line). Classic builds them with
    // alignByGeometry = true, which vertically centres each Text on ITS OWN glyph bounds — so a string with
    // descenders ("Miyuki", "Empty", "Falconry") is pushed UP by ~half the descender while a digit-only rank ("1")
    // is not, and the rank reads a pixel or two low (in-game 2026-11-17). Font-metric centring (false) places every
    // same-size label on the same baseline whatever its glyphs. Build-time only (no per-refresh cost); the share %
    // and empty hint stand alone on their side, so they keep geometry centring. Classic is untouched.
    private static void ShareTopLineBaseline(MeterRowBinding b)
    {
        if (b.Rank != null) b.Rank.alignByGeometry = false;
        if (b.Name != null) b.Name.alignByGeometry = false;
        if (b.ClassName != null) b.ClassName.alignByGeometry = false;
        if (b.Spec != null) b.Spec.alignByGeometry = false;
    }

    // Fixed-width, right-aligned rank slot (mockup `.mr .t .rk`: text-align right) so names start at the same x down
    // the list whether the rank is "2" or "12" — Classic sizes the rank to its own text, so a two-digit rank pushed its
    // name right. The slot is TWO of the font's widest digit, measured at the rank's live font (CombatMeter sends bare
    // numbers, "1".."24" in the list and the slot number in party groups). It used to be measured from "24.", which
    // reserved the width of a dot the plugin no longer sends: every rank sat that much further from the crest than
    // the name sits from the rank (in-game 2026-11-17). Only the LayoutElement's PREFERRED width is pinned: the Text's
    // own min width (0) still applies, so a narrow row shrinks the rank exactly as before (and an inactive rank —
    // ShowRank off — is still skipped by the HLG). A longer rank ("100", the list ranks globally under a scope
    // filter) widens just that row's slot to its text (Fit, on a rank change only) instead of spilling over the crest.
    // Measured once at build, and again only on a reskin (Font Scale / font change); no per-refresh cost.
    private static RankSlot? FixRankSlot(MeterRowBinding b, WindowToken token)
    {
        if (b.Rank == null) return null;
        b.Rank.alignment = TextAnchor.MiddleRight;
        var le = b.Rank.GetComponent<LayoutElement>();   // AddMeterText always adds one; no `??` on Unity objects
        if (le == null) le = b.Rank.gameObject.AddComponent<LayoutElement>();
        var slot = new RankSlot(b.Rank, le);
        slot.Measure();
        token.ReskinActions.Add(slot.Measure);
        return slot;
    }

    internal sealed class RankSlot
    {
        private const int SlotDigits = 2;
        private readonly Text _rank;
        private readonly LayoutElement _le;
        private float _slotW;

        public RankSlot(Text rank, LayoutElement le) { _rank = rank; _le = le; }

        public void Measure()
        {
            if (_rank == null || _le == null) return;
            var keep = _rank.text;
            float widest = 0f;
            for (var c = '0'; c <= '9'; c++)   // 10 text sets, build/reskin only (digits are usually tabular; check anyway)
            {
                _rank.text = c.ToString();
                widest = Mathf.Max(widest, _rank.preferredWidth);
            }
            _slotW = Mathf.Ceil(SlotDigits * widest);   // whole px → crisp name x
            _rank.text = keep;
            Fit(keep);
        }

        /// <summary>Call after the rank text changed: a rank longer than the slot widens this row's slot to the text.</summary>
        public void Fit(string? text)
        {
            if (_rank == null || _le == null) return;
            _le.preferredWidth = text != null && text.Length > SlotDigits
                ? Mathf.Max(_slotW, Mathf.Ceil(_rank.preferredWidth)) : _slotW;
        }
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
    internal sealed partial class MeterRowRoundedSkin
    {
        public WindowThemeAssets Assets = null!;
        public Func<float>? Opacity;
        /// <summary>MeterRowElement.BackingOpacity (null = 1): extra multiplier on the backing graphics only.</summary>
        public Func<float>? Backing;
        /// <summary>The resolved backing opacity of the last poll — the binding hands it to the empty status-effect
        /// cells (they are re-coloured every poll in BindDebuffBlock anyway, so no separate diff is needed).</summary>
        public float BackingOp { get; private set; } = 1f;
        public Image Bg = null!, Ring = null!, SpineTrack = null!, BarTrack = null!;
        public Image?[] SpineImgs = null!;
        public CanvasGroup SpineGroup = null!, BarGroup = null!, RowGroup = null!;
        public LayoutElement BarLe = null!;
        public Text Rank = null!, Spec = null!, Hint = null!;
        public RankSlot? RankSlot;

        private const float TileAlpha = 0.92f, SelfAlpha = 0.95f, TrackAlpha = 0.90f, EmptyAlpha = 0.75f;
        private const float DeadAlpha = 0.55f, SelfWash = 0.20f, SelfRingAlpha = 0.75f;

        private Color _tile, _self, _recessed, _spineTrack, _accent, _muted;
        /// <summary>The faint recessed label (rank + name) of an empty slot — the binding reads it for the name.</summary>
        public Color EmptyLabel { get; private set; }
        private bool _themeDirty = true;
        private int _lastKey = -1;
        private float _lastOpacity = -1f, _lastBacking = -1f, _lastSpineW = -1f;
        private ColorRgba _lastRing = new(-2f, -2f, -2f, -2f);
        private string? _lastHint;

        public void MarkThemeDirty() { _themeDirty = true; _fitMeasure = true; }   // font may have changed → re-fit top line

        public void Apply(in MeterRowData d)
        {
            if (_themeDirty) Recompute();
            float op = Mathf.Clamp01(Opacity?.Invoke() ?? 1f);
            float bk = Backing == null ? 1f : MeterBackingRules.Resolve(Backing);
            BackingOp = bk;
            int key = (d.IsEmptySlot ? 1 : 0) | (d.IsSelf ? 2 : 0) | (d.Dead ? 4 : 0);
            if (key != _lastKey || !Mathf.Approximately(op, _lastOpacity) || bk != _lastBacking || !d.RowBorder.Equals(_lastRing))
            {
                Paint(d, op, bk);
                _lastKey = key; _lastOpacity = op; _lastBacking = bk; _lastRing = d.RowBorder;
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

        // bk = BackingOpacity: fades the backing (tile, self tile, empty-slot tile, spine + bar tracks). The self tile
        // fades too — left solid it read as a heavy block among faded rows (user: "the highlight is too solid"); it keeps
        // its accent colour, so it stays a proportionally stronger wash. The self RING is NOT faded: it is what still
        // finds your row at a low backing opacity.
        private void Paint(in MeterRowData d, float op, float bk)
        {
            bool empty = d.IsEmptySlot;
            Bg.color = empty ? Fade(_recessed, EmptyAlpha * bk, op) : Fade(d.IsSelf ? _self : _tile, (d.IsSelf ? SelfAlpha : TileAlpha) * bk, op);
            SpineTrack.color = Fade(_spineTrack, bk, op);
            BarTrack.color = Fade(_recessed, TrackAlpha * bk, op);
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

/// <summary>Pure rules for <see cref="MeterRowElement.BackingOpacity"/> (System.Math only, no Unity → unit-testable).
/// Same contract as <see cref="ContentOpacityRules"/>: clamped to [0, 1], a throw or NaN = 1 (a broken plugin delegate
/// must never strip the row's backing).</summary>
internal static class MeterBackingRules
{
    public static float Resolve(Func<float> backing) => ContentOpacityRules.Resolve(backing);
}
