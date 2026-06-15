using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// WindowBuilder leaf for the bespoke CombatMeter row (<see cref="MeterRowElement"/>). Reproduces the IMGUI
/// MeterRowView geometry in retained-mode uGUI so the meter keeps its distinct borderless look: a dark
/// self-backing bg (+ self highlight), a left HP spine that drains from the bottom, a class crest, a
/// name·spec·share top line, and a role-coloured metric bar with a per-second (left) / total (right) overlay,
/// plus an offline scrim. <see cref="MeterRowData"/> is poll-diffed via <see cref="MeterRowBinding"/> on the
/// window refresh tick (no per-frame animator — the bar/spine fractions update at the capped cadence; the
/// IMGUI sheen sweep is intentionally dropped as it needs per-frame animation).
/// </summary>
internal sealed partial class WindowBuilder
{
    private const float MeterRowHeight = 48f;
    private const float MeterSpineW    = 3f;
    private const float MeterCrestW    = 22f;
    private const float MeterPad       = 6f;
    private const float MeterGap       = 7f;
    private const float MeterBarH      = 14f;

    // Opaque-enough backings to stay readable over a bright world (the 0.30/0.16-alpha originals washed out).
    private static readonly Color MeterRowBg     = new(0.05f, 0.06f, 0.08f, 0.66f);
    private static readonly Color MeterSelfBg    = new(0.12f, 0.30f, 0.33f, 0.70f);   // dark teal — self highlight
    private static readonly Color MeterSelfBdr   = new(0.45f, 0.82f, 0.87f, 0.90f);
    private static readonly Color MeterSpineBg   = new(0f, 0f, 0f, 0.40f);
    private static readonly Color MeterTrackBg   = new(1f, 1f, 1f, 0.07f);
    private static readonly Color MeterScrim     = new(0.05f, 0.06f, 0.08f, 0.50f);
    private static readonly Color MeterNameCol   = new(0.92f, 0.94f, 0.95f, 1f);
    private static readonly Color MeterSpecCol   = new(0.68f, 0.72f, 0.74f, 1f);
    private static readonly Color MeterShareCol  = new(0.81f, 0.84f, 0.86f, 1f);
    private static readonly Color MeterClassCol  = new(0.50f, 0.82f, 0.75f, 1f);   // teal base-class line
    private static readonly Color MeterScoreBg   = new(0.47f, 0.35f, 0.08f, 0.35f);// gold ability-score pill bg
    private static readonly Color MeterScoreCol  = new(1.00f, 0.83f, 0.47f, 1f);   // gold ability-score text
    internal static readonly Color MeterDeadName = new(0.71f, 0.63f, 0.63f, 1f);   // muted name when dead
    private static readonly Color MeterStrikeCol = new(0.85f, 0.33f, 0.30f, 0.90f);// red strike line over a dead name

    // Small drawn skull (12×12) for the dead-state marker — the in-game OS font lacks ☠, so it's a texture like
    // the leader flag. Bone silhouette + dark eye sockets + a toothed jaw; built once, shared by every row.
    private Texture2D? _deadTex;
    private Texture2D DeadMarkTexture()
    {
        if (_deadTex != null) return _deadTex;
        var bone = new Color(0.90f, 0.88f, 0.82f, 1f);
        var eye  = new Color(0.12f, 0.10f, 0.10f, 1f);
        var clear = new Color(0f, 0f, 0f, 0f);
        // 12 rows top→bottom: '#'=bone, 'o'=eye socket, '.'=transparent.
        string[] art =
        {
            "...######...",
            "..########..",
            ".##########.",
            ".##########.",
            ".#oo####oo#.",
            ".#oo####oo#.",
            "..########..",
            "...######...",
            "...#.##.#...",
            "...#.##.#...",
            "....####....",
            "....####....",
        };
        const int w = 12, h = 12;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
        { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            char c = art[h - 1 - y][x];   // art[0] is the top row; texture y is bottom-up
            t.SetPixel(x, y, c == '#' ? bone : c == 'o' ? eye : clear);
        }
        t.Apply();
        _deadTex = t;
        return t;
    }

    private void BuildMeterRow(MeterRowElement el, Transform parent, WindowToken token)
    {
        var row = UGuiPrimitives.NewChild("MeterRow", parent);
        var le = row.AddComponent<LayoutElement>();
        le.preferredHeight = le.minHeight = MeterRowHeight; le.flexibleWidth = 1f;

        var (bg, border, spineFill, spine) = BuildMeterBackplate(row.transform);

        // Content — inset past the spine; top line + bar stacked.
        var content = UGuiPrimitives.NewChild("Content", row.transform);
        UGuiPrimitives.Stretch(content);
        var vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset((int)(MeterSpineW + MeterPad), (int)MeterPad, 5, 5);
        vlg.spacing = 2f; vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false; vlg.childAlignment = TextAnchor.UpperLeft;

        var (crest, crestCell, deadMark, rank, name, nameStrike, className, spec, specGo, score, scoreGo, share, shareGo, leaderGo, imagine, imagineGroup, topLine) = BuildMeterTopLine(content.transform, token);
        var (fillRect, fillImg, primary, secondary, secondaryGo) = BuildMeterBar(content.transform, token);

        // Right-column host for the "RightColumn" imagine position — an ignore-layout cell on the row's right
        // edge spanning full height; the binding re-parents the imagine group here (vertically centred) and
        // reserves content right-padding so the bar/text don't run under it. Empty until that position is set.
        var rightCol = UGuiPrimitives.NewChild("ImagineCol", row.transform);
        rightCol.AddComponent<LayoutElement>().ignoreLayout = true;
        var rcrt = rightCol.GetComponent<RectTransform>();
        rcrt.anchorMin = new Vector2(1f, 0f); rcrt.anchorMax = new Vector2(1f, 1f); rcrt.pivot = new Vector2(1f, 0.5f);
        rcrt.sizeDelta = new Vector2(58f, -6f); rcrt.anchoredPosition = new Vector2(-MeterPad, 0f);
        var rcHlg = rightCol.AddComponent<HorizontalLayoutGroup>();
        rcHlg.childControlWidth = true; rcHlg.childControlHeight = true;
        rcHlg.childForceExpandWidth = false; rcHlg.childForceExpandHeight = false; rcHlg.childAlignment = TextAnchor.MiddleRight;

        // Offline scrim — drawn last (on top), toggled by Offline.
        var scrim = AddStretchedImage(row.transform, "Scrim", MeterScrim, ignoreLayout: true).gameObject;

        token.MeterRows.Add(new MeterRowBinding
        {
            Data = el.Data, ContentVlg = vlg,
            Bg = bg, SelfBorder = border, SpineFill = spineFill, SpineGo = spine,
            Crest = crest, CrestCellGo = crestCell, DeadMarkGo = deadMark, Rank = rank, RankGo = rank.gameObject,
            Name = name, NameStrikeGo = nameStrike,
            ClassName = className, ClassNameGo = className.gameObject, Spec = spec, SpecGo = specGo,
            Score = score, ScoreGo = scoreGo, Share = share, ShareGo = shareGo, LeaderGo = leaderGo,
            BarFillRect = fillRect, BarFillImg = fillImg, Primary = primary, PrimaryGo = primary.gameObject, Secondary = secondary, SecondaryGo = secondaryGo, Scrim = scrim,
            Imagine0Cell = imagine[0], Imagine1Cell = imagine[1],
            ImagineGroup = imagineGroup.transform, TopLine = topLine, RightColHost = rightCol.transform,
        });

        if (el.OnRightClick is { } rc)
            RegisterRightClick?.Invoke(row.GetComponent<RectTransform>(), () => rc());
    }

    // Bg (self-backing) + self-highlight border + HP spine — the ignore-layout backplate drawn behind content.
    private (Image bg, GameObject border, Image spineFill, GameObject spine) BuildMeterBackplate(Transform row)
    {
        var bg = AddStretchedImage(row, "Bg", MeterRowBg, ignoreLayout: true);

        var border = UGuiPrimitives.NewChild("SelfBorder", row);
        border.AddComponent<LayoutElement>().ignoreLayout = true;
        UGuiPrimitives.Stretch(border);
        AddEdge(border.transform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));   // top
        AddEdge(border.transform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0f));   // bottom
        AddEdge(border.transform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f));   // left
        AddEdge(border.transform, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f));   // right

        var spine = UGuiPrimitives.NewChild("Spine", row);
        spine.AddComponent<LayoutElement>().ignoreLayout = true;
        var srt = spine.GetComponent<RectTransform>();
        srt.anchorMin = new Vector2(0f, 0f); srt.anchorMax = new Vector2(0f, 1f); srt.pivot = new Vector2(0f, 0.5f);
        srt.sizeDelta = new Vector2(MeterSpineW, -4f); srt.anchoredPosition = Vector2.zero;
        var spineBg = spine.AddComponent<Image>(); spineBg.color = MeterSpineBg; spineBg.raycastTarget = false;
        // HP fill: a bottom-anchored solid rect whose HEIGHT is the HP fraction (the binding drives anchorMax.y).
        // NOT Image.Type.Filled — a uGUI Image with no sprite ignores fillAmount and draws a FULL quad, so the
        // migrated Filled spine stayed full regardless of HP. Anchor-resize needs no sprite and mirrors how the
        // role bar clips its width. Build-default colour is transparent: the binding paints the real HP colour
        // only once it differs from the struct default, so an empty placeholder row keeps an invisible spine.
        var fillGo = UGuiPrimitives.NewChild("Fill", spine.transform);
        var frt = fillGo.GetComponent<RectTransform>();
        frt.anchorMin = new Vector2(0f, 0f); frt.anchorMax = new Vector2(1f, 1f); frt.pivot = new Vector2(0.5f, 0f);
        frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;
        var spineFill = fillGo.AddComponent<Image>(); spineFill.color = Color.clear; spineFill.raycastTarget = false;
        return (bg, border, spineFill, spine);
    }

    private static readonly Color MeterLeaderCol = new(0.96f, 0.78f, 0.20f, 1f);   // gold party-leader flag

    // Small drawn flag: a thin pole down the left + a gold pennant filling the top ~⅔ (a triangular tail on its
    // right edge so it reads as a flag, not a block). Built once, shared by every row's leader marker.
    private Texture2D? _leaderTex;
    private Texture2D LeaderFlagTexture()
    {
        if (_leaderTex != null) return _leaderTex;
        // 12×14 flag: a thin pole down the left, a finial knob on top, and a solid gold banner on the upper
        // pole with a swallowtail (notch) cut into its right edge so it reads cleanly as a flag at this size.
        const int w = 12, h = 14;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
        { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var pole = new Color(0.82f, 0.85f, 0.90f, 1f);
        var clear = new Color(0f, 0f, 0f, 0f);
        const int bannerBot = 7, bannerTop = 13, left = 2, right = 10, mid = (bannerBot + bannerTop) / 2;  // y is bottom-up
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = clear;
            if (x <= 1 && y <= 12) c = pole;                          // pole (full height under the finial)
            else if (y == 13 && x <= 2) c = pole;                     // small finial knob on top of the pole
            if (y >= bannerBot && y <= bannerTop && x >= left)
            {
                // Swallowtail: a V-notch cut into the MIDDLE of the right edge (full reach at top/bottom rows,
                // cut shortest at the vertical middle) → a forked flag tail.
                var notch = right - (3 - System.Math.Abs(y - mid));
                if (x <= notch) c = MeterLeaderCol;
            }
            t.SetPixel(x, y, c);
        }
        t.Apply();
        _leaderTex = t;
        return t;
    }

    private (RawImage crest, GameObject crestCell, GameObject deadMark, Text rank, Text name, GameObject nameStrike, Text className, Text spec, GameObject specGo, Text score, GameObject scoreGo, Text share, GameObject shareGo, GameObject leaderGo, ImagineCell[] imagine, GameObject imagineGroup, Transform topLine)
        BuildMeterTopLine(Transform parent, WindowToken token)
    {
        var line = UGuiPrimitives.NewChild("Top", parent);
        var hlg = line.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 4f; hlg.childControlWidth = true; hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false; hlg.childAlignment = TextAnchor.MiddleLeft;
        line.AddComponent<LayoutElement>().minHeight = MeterCrestW;

        // 22×22 layout cell with a centre-anchored image child so the binding can letterbox a non-square
        // atlas cell (AspectFit) inside the box instead of stretching it.
        var crestGo = UGuiPrimitives.NewChild("Crest", line.transform);
        UGuiPrimitives.SetPreferred(crestGo, MeterCrestW, MeterCrestW);
        var crestImgGo = UGuiPrimitives.NewChild("Img", crestGo.transform);
        var crt = crestImgGo.GetComponent<RectTransform>();
        crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 0.5f);
        crt.sizeDelta = new Vector2(MeterCrestW, MeterCrestW);
        var crest = crestImgGo.AddComponent<RawImage>(); crest.raycastTarget = false; crest.enabled = false;

        // Order: crest · skull · rank ("2.") · leader flag · name. The flag sits between rank and name.
        var deadGo = AddDeadMark(line.transform);
        var rank = AddMeterText(token, line.transform, ("Rank", 12, true, MeterNameCol));

        // Party-leader flag — a small drawn flag. Font-independent (a glyph like ⚑ isn't in the in-game OS font).
        // Toggled by MeterRowData.IsLeader; when inactive the HLG skips it (no gap).
        var leaderGo = UGuiPrimitives.NewChild("Leader", line.transform);
        UGuiPrimitives.SetPreferred(leaderGo, 12f, 14f);
        var lf = leaderGo.AddComponent<RawImage>(); lf.texture = LeaderFlagTexture(); lf.raycastTarget = false;
        leaderGo.SetActive(false);

        var name = AddMeterText(token, line.transform, ("Name", 12, true, MeterNameCol));
        var nameStrike = AddNameStrike(name);
        // Optional base-class line (between name and spec) — hidden until MeterRowData.ShowClassName.
        var className = AddMeterText(token, line.transform, ("Class", 10, false, MeterClassCol));
        className.gameObject.SetActive(false);
        var spec = AddMeterText(token, line.transform, ("Spec", 10, false, MeterSpecCol));
        // Ability-score pill (after spec) — hidden until MeterRowData.ShowAbilityScore (deferred: no wire yet).
        var (score, scoreGo) = BuildScorePill(token, line.transform);
        AddFlexSpacer(line.transform);
        var share = AddMeterText(token, line.transform, ("Share", 11, true, MeterShareCol));
        share.alignment = TextAnchor.MiddleRight;

        // Battle-Imagine cells live in a re-parentable group (so MeterRowData.ImaginePosition can move the
        // whole cluster between top-right / left / a right column). Default spot: end of the top line = top-right.
        var imagineGroup = UGuiPrimitives.NewChild("Imagines", line.transform);
        var igHlg = imagineGroup.AddComponent<HorizontalLayoutGroup>();
        igHlg.spacing = 4f; igHlg.childControlWidth = true; igHlg.childControlHeight = true;
        igHlg.childForceExpandWidth = false; igHlg.childForceExpandHeight = false; igHlg.childAlignment = TextAnchor.MiddleRight;
        var imagine = new[] { BuildImagineCell(token, imagineGroup.transform), BuildImagineCell(token, imagineGroup.transform) };

        return (crest, crestGo, deadGo, rank, name, nameStrike, className, spec, spec.gameObject, score, scoreGo, share, share.gameObject, leaderGo, imagine, imagineGroup, line.transform);
    }

    // Dead-state skull marker cell (12×12 RawImage, leads the row). Built inactive; toggled by Dead.
    private GameObject AddDeadMark(Transform line)
    {
        var deadGo = UGuiPrimitives.NewChild("DeadMark", line);
        UGuiPrimitives.SetPreferred(deadGo, 12f, 12f);
        var dm = deadGo.AddComponent<RawImage>(); dm.texture = DeadMarkTexture(); dm.raycastTarget = false;
        deadGo.SetActive(false);
        return deadGo;
    }

    // Strike line over the name (uGUI Text has no strikethrough) — a thin ignore-layout Image spanning the name
    // width, vertically centred. Built inactive; toggled by Dead.
    private static GameObject AddNameStrike(Text name)
    {
        var go = UGuiPrimitives.NewChild("Strike", name.transform);
        go.AddComponent<LayoutElement>().ignoreLayout = true;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0.5f); rt.anchorMax = new Vector2(1f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(0f, 1.6f); rt.anchoredPosition = Vector2.zero;
        var img = go.AddComponent<Image>(); img.color = MeterStrikeCol; img.raycastTarget = false;
        go.SetActive(false);
        return go;
    }

    // Gold ability-score pill: a content-sized rounded-ish chip (flat fill) with centred gold text. Hidden by
    // default; the binding shows it + sets the text when MeterRowData.ShowAbilityScore. Built inactive.
    private (Text text, GameObject go) BuildScorePill(WindowToken token, Transform parent)
    {
        var pill = UGuiPrimitives.NewChild("Score", parent);
        var hlg = pill.AddComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(5, 5, 0, 0); hlg.childControlWidth = true; hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false; hlg.childAlignment = TextAnchor.MiddleCenter;
        pill.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        var bg = pill.AddComponent<Image>(); bg.color = MeterScoreBg; bg.raycastTarget = false;
        var txt = AddMeterText(token, pill.transform, ("ScoreTxt", 10, true, MeterScoreCol));
        pill.SetActive(false);
        return (txt, pill);
    }

    private (RectTransform fillRect, Image fillImg, Text primary, Text secondary, GameObject secondaryGo) BuildMeterBar(Transform parent, WindowToken token)
    {
        var bar = UGuiPrimitives.NewChild("Bar", parent);
        bar.AddComponent<LayoutElement>().preferredHeight = MeterBarH;
        var track = bar.AddComponent<Image>(); track.color = MeterTrackBg; track.raycastTarget = false;

        // Filled region = a width-clipped container (anchorMax.x = fraction) with a RectMask2D, so the role
        // fill AND the scrolling sheen are clipped to the filled width — the IMGUI GUI.BeginClip(fill) analog.
        var clipGo = UGuiPrimitives.NewChild("FillClip", bar.transform);
        var clipRt = clipGo.GetComponent<RectTransform>();
        clipRt.anchorMin = new Vector2(0f, 0f); clipRt.anchorMax = new Vector2(1f, 1f); clipRt.pivot = new Vector2(0f, 0.5f);
        clipRt.offsetMin = Vector2.zero; clipRt.offsetMax = Vector2.zero;
        clipGo.AddComponent<RectMask2D>();

        var fillImg = AddStretchedImage(clipGo.transform, "Fill", Color.clear, ignoreLayout: false);

        // Sheen: a soft white band scrolled left→right within the clipped fill (per-frame, ticker-driven via the
        // pulse hook). Null in the sandbox → renders static at its rest position.
        var sheenGo = UGuiPrimitives.NewChild("Sheen", clipGo.transform);
        var sheenRt = sheenGo.GetComponent<RectTransform>();
        sheenRt.anchorMin = new Vector2(0f, 0f); sheenRt.anchorMax = new Vector2(0f, 1f); sheenRt.pivot = new Vector2(0f, 0.5f);
        sheenRt.sizeDelta = new Vector2(60f, 0f); sheenRt.anchoredPosition = Vector2.zero;
        var sheen = sheenGo.AddComponent<RawImage>(); sheen.texture = SheenTexture(); sheen.raycastTarget = false; sheen.color = Color.white;
        System.Action<float> sweep = _ => DriveSheen(clipRt, sheenRt, sheen);
        token.Pulses.Add(sweep); _registerPulse?.Invoke(sweep);

        // Overlay texts span the FULL bar (not the clip); left = per-second, right = total. 5-px horizontal inset.
        var primary = AddOverlayText(token, bar.transform, "Primary", TextAnchor.MiddleLeft);
        var secondary = AddOverlayText(token, bar.transform, "Secondary", TextAnchor.MiddleRight);
        return (clipRt, fillImg, primary, secondary, secondary.gameObject);
    }

    private const float SheenPeriod = 2.4f;

    // Move the sheen band across the (variable-width) clipped fill from time — mirrors MeterRowView.DrawSheen.
    private static void DriveSheen(RectTransform clip, RectTransform sheen, RawImage img)
    {
        if (clip == null || sheen == null || img == null || !sheen.gameObject.activeInHierarchy) return;
        float w = clip.rect.width;
        if (w < 8f) { if (img.enabled) img.enabled = false; return; }
        if (!img.enabled) img.enabled = true;
        float band = Mathf.Max(50f, w * 0.55f);
        float p = (Time.realtimeSinceStartup % SheenPeriod) / SheenPeriod;
        sheen.sizeDelta = new Vector2(band, 0f);
        sheen.anchoredPosition = new Vector2(-band + p * (w + band), 0f);
    }

    // Soft white horizontal gradient band (alpha peaks at the centre) — built once, shared by every row's sheen.
    private Texture2D? _sheenTex;
    private Texture2D SheenTexture()
    {
        if (_sheenTex != null) return _sheenTex;
        const int w = 64, h = 4;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
        { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (var x = 0; x < w; x++)
        {
            float d = Mathf.Abs(x - w * 0.5f) / (w * 0.5f);
            float a = Mathf.Clamp01(1f - d); a = a * a * 0.42f;
            var c = new Color(1f, 1f, 1f, a);
            for (var y = 0; y < h; y++) t.SetPixel(x, y, c);
        }
        t.Apply();
        _sheenTex = t;
        return t;
    }

    private Text AddOverlayText(WindowToken token, Transform parent, string nm, TextAnchor anchor, int baseSize = 10)
    {
        var go = UGuiPrimitives.NewChild(nm, parent);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(5f, 0f); rt.offsetMax = new Vector2(-5f, 0f);
        var txt = go.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(txt, Scaled(baseSize), anchor, bold: true);
        ApplyMenuFont(txt); txt.color = Color.white; txt.alignByGeometry = true; txt.horizontalOverflow = HorizontalWrapMode.Overflow;
        RegisterTextSizeReskin(token, txt, baseSize);
        return txt;
    }

    // Font-size-only reskin: re-applies Scaled(baseSize) (+ font) on a FontScale/theme change WITHOUT touching
    // colour, so the global Font Scale slider rescales the meter live. (RegisterTextReskin can't be reused here —
    // it forces a theme colour, which would clobber the meter's data-driven role/share/DPS colours.)
    private void RegisterTextSizeReskin(WindowToken token, Text txt, int baseSize)
        => token.ReskinActions.Add(() =>
        {
            if (txt == null) return;
            txt.fontSize = Scaled(baseSize);
            if (_assets.MenuFont != null) txt.font = _assets.MenuFont;
        });

    private Text AddMeterText(WindowToken token, Transform parent, (string nm, int size, bool bold, Color col) t)
    {
        var go = UGuiPrimitives.NewChild(t.nm, parent);
        var txt = go.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(txt, Scaled(t.size), TextAnchor.MiddleLeft, bold: t.bold);
        ApplyMenuFont(txt); txt.color = t.col; txt.alignByGeometry = true;
        txt.horizontalOverflow = HorizontalWrapMode.Overflow;
        go.AddComponent<LayoutElement>().flexibleWidth = 0f;
        RegisterTextSizeReskin(token, txt, t.size);
        return txt;
    }

    private static void AddFlexSpacer(Transform parent)
    {
        var go = UGuiPrimitives.NewChild("Spacer", parent);
        go.AddComponent<LayoutElement>().flexibleWidth = 1f;
    }

    private static Image AddStretchedImage(Transform parent, string nm, Color col, bool ignoreLayout)
    {
        var go = UGuiPrimitives.NewChild(nm, parent);
        if (ignoreLayout) go.AddComponent<LayoutElement>().ignoreLayout = true;
        UGuiPrimitives.Stretch(go);
        var img = go.AddComponent<Image>(); img.color = col; img.raycastTarget = false;
        return img;
    }

    // Row with a role-coloured accent backdrop (DrawRowAccent analog): a faint share-fraction wash + a 3-px
    // left stripe behind the child content. Used by the History/Skill tables.
    private void BuildAccentRow(AccentRowElement el, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("AccentRow", parent);
        go.AddComponent<LayoutElement>().flexibleWidth = 1f;
        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(6, 6, 3, 3); vlg.spacing = 0f;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false; vlg.childAlignment = TextAnchor.UpperLeft;
        go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Faint share-fraction wash (ignore-layout, behind the child; width = Share via anchorMax.x).
        var shareGo = UGuiPrimitives.NewChild("Share", go.transform);
        shareGo.AddComponent<LayoutElement>().ignoreLayout = true;
        var srt = shareGo.GetComponent<RectTransform>();
        srt.anchorMin = new Vector2(0f, 0f); srt.anchorMax = new Vector2(1f, 1f); srt.pivot = new Vector2(0f, 0.5f);
        srt.offsetMin = Vector2.zero; srt.offsetMax = Vector2.zero;
        var shareImg = shareGo.AddComponent<Image>(); shareImg.raycastTarget = false;

        // 3-px role stripe at the left edge.
        var stripeGo = UGuiPrimitives.NewChild("Stripe", go.transform);
        stripeGo.AddComponent<LayoutElement>().ignoreLayout = true;
        var strt = stripeGo.GetComponent<RectTransform>();
        strt.anchorMin = new Vector2(0f, 0f); strt.anchorMax = new Vector2(0f, 1f); strt.pivot = new Vector2(0f, 0.5f);
        strt.sizeDelta = new Vector2(3f, 0f); strt.anchoredPosition = Vector2.zero;
        var stripeImg = stripeGo.AddComponent<Image>(); stripeImg.raycastTarget = false;

        BuildElement(el.Child, go.transform, token);

        token.AccentRows.Add(new AccentRowBinding
        {
            ShareRect = srt, ShareImg = shareImg, Stripe = stripeImg, StripeFn = el.Stripe, ShareFn = el.Share,
        });
    }

    private static void AddEdge(Transform parent, Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 size)
    {
        var go = UGuiPrimitives.NewChild("Edge", parent);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = pivot;
        // size encodes which dimension is the 1-px line: x=1 → vertical edge, y=1 → horizontal edge.
        rt.sizeDelta = new Vector2(size.x > 0.5f ? 1f : 0f, size.y > 0.5f ? 1f : 0f);
        rt.anchoredPosition = Vector2.zero;
        var img = go.AddComponent<Image>(); img.color = MeterSelfBdr; img.raycastTarget = false;
    }
}
