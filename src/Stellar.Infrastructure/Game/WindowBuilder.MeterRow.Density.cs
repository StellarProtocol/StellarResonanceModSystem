using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// MeterRowElement.Density (Compact / Mini) — one-line sizes of the Rounded meter row (approved mockups
// combatmeter-compact.html + combatmeter-compact-raid20.html, 2026-11-17). Like the Rounded skin it RESHAPES the built
// row instead of building a second row type, so every data path (name fit + Thai twin, dead fade, offline scrim, self /
// speaking ring, empty slot, label styles, effect clicks, right-click, drag) keeps working:
//   • the metric bar leaves the content column and is stretched over the WHOLE row, behind the spine and the text, so the
//     bar is the row's fill (Compact: role colour @55 % over the tile, track off; Mini: @75 % over a track-coloured tile);
//   • the two bar values move out of the bar into the top line's right end ([total][per-second], Mini "total · per-sec");
//   • elements the size doesn't draw are removed once at build (Mini: crest, spine, flag) or masked per poll by
//     MeterRowDensityRules.Mask, a stack copy of MeterRowData (no allocation) — so a plugin may keep sending them.
// Normal rows never reach this file (binding.Density stays null → byte-identical to before).
internal sealed partial class WindowBuilder
{
    private const int   CompactTileR = 5, MiniTileR = 4;       // mockup `.c` r5 / `.m` r4 (tile, fill, ring share it)
    private const float CompactCrestW = 16f;
    private const float CompactInset  = 3f;                   // spine inset from the tile edge (mockup `.sp2` margin 3)
    private const int   CompactPadR = 4, MiniPad = 5;          // mockup `.c` padding-right 4, `.m` padding 0 5
    private const float CompactFxGap = 5f;                    // gap between the per-second value and the effect tiles
    private const float CompactFillA = 0.55f, MiniFillA = 0.75f;
    private const int   CompactFont = 12, MiniFont = 11;
    private const string PerSecondSample = "888.8K";           // widest usual per-second text → the fixed slot width

    private void ApplyMeterRowDensity(MeterRowBinding b, GameObject row, WindowToken token, MeterRowDensity density)
    {
        if (b.Rounded == null || density == MeterRowDensity.Normal) return;   // Rounded only; Normal = unchanged
        bool mini = density == MeterRowDensity.Mini;
        int r = mini ? MiniTileR : CompactTileR;
        b.RowLe.preferredHeight = b.RowLe.minHeight = MeterRowDensityRules.RowHeight(density);
        SetRoundSprite(b.Bg, _assets.BubbleBg(r));
        if (b.Scrim != null) SetRoundSprite(b.Scrim.GetComponent<Image>(), _assets.BubbleBg(r));
        SetRoundSprite(b.Rounded.Ring, _assets.BubbleRingHalfPx(r, 3));
        StretchBarBehindRow(b, row, r, mini);

        var vlg = b.ContentVlg;   // the bar left it: only the top line remains, centred in the short tile
        vlg.padding = new RectOffset(mini ? MiniPad : vlg.padding.left, mini ? MiniPad : CompactPadR, 0, 0);
        var lineLe = b.TopLine.GetComponent<LayoutElement>();
        if (lineLe != null) lineLe.minHeight = mini ? 0f : CompactCrestW;
        b.ImagineGroup.gameObject.SetActive(false);   // never drawn in either size (its HLG gap would still count)

        if (mini) StripMiniExtras(b, token);
        else ShrinkCompactCells(b);
        b.Density = BuildDensityValues(b, token, density);
    }

    // Bar → a full-row, ignore-layout fill directly under the tile (sibling 1 = right after Bg, so the spine, text, ring and
    // scrim all draw over it). Same GameObjects, so the binding's fraction / colour / shield / sheen writes are unchanged.
    private void StretchBarBehindRow(MeterRowBinding b, GameObject row, int r, bool mini)
    {
        var barGo = b.BarFillRect.parent.gameObject;
        var le = barGo.GetComponent<LayoutElement>();
        if (le != null) le.ignoreLayout = true;
        var rt = barGo.GetComponent<RectTransform>()!;   // IL2CPP-safe (never cast a .parent wrapper)
        rt.SetParent(row.transform, false);
        rt.SetSiblingIndex(1);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        SetRoundSprite(b.BarFillImg, _assets.BubbleBg(r));
        SetRoundSprite(b.BarShieldImg, _assets.BubbleBg(r));
        b.Rounded!.BarTrack.enabled = false;   // Compact: the tile shows through; Mini: the tile itself is painted as the track
        b.Rounded.BgAsTrack = mini;
        b.FillTint = new Color(1f, 1f, 1f, mini ? MiniFillA : CompactFillA);
    }

    // Compact: 16 px crest, 3 px spine inset 3 px (the width itself is pinned by MeterRowDensityRules.Mask).
    private static void ShrinkCompactCells(MeterRowBinding b)
    {
        var crestLe = b.CrestCellGo.GetComponent<LayoutElement>();
        if (crestLe != null) crestLe.preferredWidth = crestLe.preferredHeight = CompactCrestW;
        b.Crest.rectTransform.sizeDelta = new Vector2(CompactCrestW, CompactCrestW);
        b.CrestBox = CompactCrestW;
        b.SpineGo.GetComponent<RectTransform>()!.anchoredPosition = new Vector2(CompactInset, 0f);
        b.SpineHeightDelta = -2f * CompactInset;
        b.SpineLeftExtra = 2f;   // spine 3..6, content from 11 → the mockup's 5 px gap
        if (b.DebuffColHost != null)
            ((RectTransform)b.DebuffColHost).anchoredPosition = new Vector2(-CompactPadR, 0f);
    }

    // Mini draws no crest, spine or leader flag: deactivate once and null the binding refs (its toggles null-check them),
    // so no poll ever re-shows them. Rank + name drop to 11 px (re-applied after any Font Scale reskin).
    private void StripMiniExtras(MeterRowBinding b, WindowToken token)
    {
        b.CrestCellGo.SetActive(false); b.CrestCellGo = null!; b.Crest = null!;
        b.SpineGo.SetActive(false); b.SpineGo = null!; b.SpineFill = null!; b.ShieldFill = null;
        b.LeaderGo.SetActive(false); b.LeaderGo = null!;
        SetMeterTextSize(token, b.Rank, MiniFont);
        SetMeterTextSize(token, b.Name, MiniFont);
        var slot = b.Rounded!.RankSlot;
        if (slot != null) { slot.Measure(); token.ReskinActions.Add(slot.Measure); }   // after the 11 px reskin above
    }

    // The bar values leave the bar for the top line's right end, after the flex spacer:
    //   Compact [total (bold)][per-second (regular, fixed slot, right-aligned)]   Mini [total][·][per-second]
    // Font-metric centring (alignByGeometry off) so they share the name's baseline (KB: alignByGeometry breaks a shared
    // baseline). Their Outline components travel with them, so the binding's label style still applies.
    private MeterRowDensitySkin BuildDensityValues(MeterRowBinding b, WindowToken token, MeterRowDensity density)
    {
        bool mini = density == MeterRowDensity.Mini;
        var values = UGuiPrimitives.NewChild("Values", b.TopLine);
        var hlg = values.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = mini ? 3f : CompactFxGap; hlg.childControlWidth = true; hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false; hlg.childAlignment = TextAnchor.MiddleRight;
        int size = mini ? MiniFont : CompactFont;
        MoveValue(b.Secondary, values.transform, token, size);
        Text? sep = null;
        if (mini)
        {
            sep = AddMeterText(token, values.transform, ("Sep", MiniFont, true, Color.white));
            LegacyText.SetAscii(sep, "·"); sep.alignByGeometry = false;
            UGuiPrimitives.AddReadabilityOutline(sep.gameObject).enabled = false;
        }
        MoveValue(b.Primary, values.transform, token, size);
        if (!mini) ShapePerSecond(b.Primary, token);
        return new MeterRowDensitySkin
        {
            Density = density, ValuesGo = values, SepGo = sep != null ? sep.gameObject : null,
            SepOutline = sep != null ? sep.GetComponent<Outline>() : null,
            PadRight = mini ? MiniPad : CompactPadR,
        };
    }

    private void MoveValue(Text t, Transform values, WindowToken token, int size)
    {
        t.transform.SetParent(values, false);
        t.alignByGeometry = false;
        SetMeterTextSize(token, t, size);
    }

    // Compact per-second: regular weight, slightly softer white (mockup `.ps` #e6dbe9), right-aligned in a slot as wide as
    // "888.8K" so the totals line up down the list. MIN width (a longer value still grows its own slot, never overlaps).
    private void ShapePerSecond(Text ps, WindowToken token)
    {
        ps.fontStyle = FontStyle.Normal;
        ps.alignment = TextAnchor.MiddleRight;
        ps.color = new Color(1f, 1f, 1f, 0.85f);
        var le = ps.gameObject.AddComponent<LayoutElement>();
        void Measure()
        {
            if (ps == null || le == null) return;
            var keep = ps.text;
            LegacyText.SetAscii(ps, PerSecondSample);   // a digits sample, measured
            le.minWidth = Mathf.Ceil(ps.preferredWidth);
            LegacyText.SetRouted(ps, keep);   // restore what the binding routed there
        }
        Measure();
        token.ReskinActions.Add(Measure);   // after the size reskin registered by MoveValue
    }

    // Sets a meter label's size now AND after every Font Scale / font reskin. Registered after the label's own base-size
    // reskin (AddMeterText / AddOverlayText), so this one wins.
    private void SetMeterTextSize(WindowToken token, Text t, int size)
    {
        t.fontSize = Scaled(size);
        RegisterTextSizeReskin(token, t, size);
    }

    // Per-poll part of a Compact / Mini row. Mask() runs FIRST in MeterRowBinding.Apply, so every later step (Rounded
    // paint, spine shape, visibility, debuffs, top-line fit) sees the masked snapshot.
    internal sealed class MeterRowDensitySkin
    {
        public MeterRowDensity Density;
        public GameObject ValuesGo = null!;
        public GameObject? SepGo;
        public Outline? SepOutline;
        public int PadRight;
        private int _lastValues = -1, _lastSep = -1, _lastSepOutline = -1;

        public void Prepare(ref MeterRowData d)
        {
            MeterRowDensityRules.Mask(ref d, Density);
            int vals = d.IsEmptySlot ? 0 : 1;   // the bar's CanvasGroup hid the values on an empty slot; they left the bar
            if (vals != _lastValues) { ValuesGo.SetActive(vals == 1); _lastValues = vals; }
            if (SepGo == null) return;
            int sep = d.ShowPrimary && d.ShowSecondary && !string.IsNullOrEmpty(d.SecondaryValue) ? 1 : 0;
            if (sep != _lastSep) { SepGo.SetActive(sep == 1); _lastSep = sep; }
            int ol = d.LabelStyle == MeterLabelStyle.Plain ? 0 : 1;   // Rounded: Shadow falls back to the halo too
            if (SepOutline != null && ol != _lastSepOutline) { SepOutline.enabled = ol == 1; _lastSepOutline = ol; }
        }
    }

    internal sealed partial class MeterRowBinding
    {
        public MeterRowDensitySkin? Density;        // Compact / Mini (null = Normal, byte-identical)
        public Color FillTint = Color.white;        // multiplied into the bar fill colour (Compact / Mini fill alpha)
        public float CrestBox = MeterCrestW;        // crest letterbox edge (Compact 16)

        // Compact / Mini own the content padding and the effect host: right padding = edge pad (+ effect block + gap when
        // shown), top/bottom 0; the row height was fixed at build. Keyed on ShowDebuffs only (the masked cell size and
        // column count are constants), so it relays out only when the effects toggle flips.
        private void ApplySlimLayout(in MeterRowData d)
        {
            int key = d.ShowDebuffs ? 1 : 0;
            if (_lastDebuffLayout == key) return;
            _lastDebuffLayout = key;
            float blockW = DebuffBlockPx(d);
            int right = Density!.PadRight + (d.ShowDebuffs ? (int)(blockW + CompactFxGap) : 0);
            var p = ContentVlg.padding;
            ContentVlg.padding = new RectOffset(p.left, right, 0, 0);
            if (DebuffColHost != null)
            {
                var rt = (RectTransform)DebuffColHost;
                rt.sizeDelta = new Vector2(blockW, rt.sizeDelta.y);
            }
        }
    }
}

/// <summary>Pure rules for <see cref="MeterRowElement.Density"/> (no Unity → unit-testable): the row heights and the
/// per-poll mask of what a Compact / Mini row does not draw.</summary>
internal static class MeterRowDensityRules
{
    public const float NormalRowH = 48f, CompactRowH = 24f, MiniRowH = 17f;
    public const float CompactEffectCell = 16f, CompactSpineW = 3f;
    public const int CompactEffectCols = 3, CompactEffectRows = 1;

    public static float RowHeight(MeterRowDensity d)
        => d == MeterRowDensity.Compact ? CompactRowH : d == MeterRowDensity.Mini ? MiniRowH : NormalRowH;

    /// <summary>Effect rows drawn by a size (the Normal block is always 2 rows).</summary>
    public static int EffectRows(MeterRowDensity d) => d == MeterRowDensity.Normal ? 2 : CompactEffectRows;

    /// <summary>Clears what <paramref name="density"/> does not draw and pins its fixed geometry. Normal = untouched.</summary>
    public static void Mask(ref MeterRowData d, MeterRowDensity density)
    {
        if (density == MeterRowDensity.Normal) return;
        d.ShowSpec = false; d.ShowClassName = false; d.ShowShare = false; d.ShowAbilityScore = false;
        d.ShowVoiceIcon = false; d.OnVoiceIconClick = null;   // no clickable (or drag-excluded) voice cell either
        d.ShowImagine = false; d.ShowImagineCooldown = false;
        d.SpineWidth = CompactSpineW;
        d.DebuffCellSize = CompactEffectCell; d.DebuffColumns = CompactEffectCols;
        if (density != MeterRowDensity.Mini) return;
        d.ShowCrest = false; d.ShowHpBar = false; d.ShowLeaderFlag = false; d.ShowDebuffs = false; d.HideSheen = true;
    }
}
