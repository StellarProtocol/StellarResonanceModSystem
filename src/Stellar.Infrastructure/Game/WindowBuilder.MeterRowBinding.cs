using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// The bespoke CombatMeter row binding — split out of WindowBuilder.Bindings.cs (which crossed the 500-LoC file
// gate once the HudOverlay shadow/dynamic-font fields were added to TextBinding/BarBinding). Pure relocation:
// the class is a nested type of the WindowBuilder partial, so every reference to WindowBuilder's other nested
// types/consts (MeterRowData, ImagineCell, MeterSpineW, MeterPad, MeterNameCol, …) resolves unchanged.
internal sealed partial class WindowBuilder
{
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
        public RawImage VoiceImg = null!;           // name-line status icon (team-voice); plugin-supplied texture
        private object? _lastVoiceTex; private Color _lastVoiceTint = new(-2f, -2f, -2f, -2f); private int _lastVoiceVis = -1;
        public GameObject TalkBorderGo = null!;     // plugin-tinted box border (e.g. green while talking)
        private ColorRgba _lastRowBorder = new(-2f, -2f, -2f, -2f);
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
        private ColorRgba _lastSelfAccent;
        private int _lastLeader = -1;
        private float _lastHp = -1f, _lastBar = -1f, _lastSpineW = -1f;
        private ColorRgba _lastHpCol, _lastRoleCol;
        private string? _lastRank, _lastName, _lastSpec, _lastShare, _lastPrimary, _lastSecondary;
        private int _lastSpecVis = -1, _lastShareVis = -1, _lastSecondaryVis = -1, _lastOffline = -1;
        private int _lastRankVis = -1, _lastCrestVis = -1, _lastSpineVis = -1, _lastPrimaryVis = -1;
        private int _lastClassVis = -1, _lastScoreVis = -1, _lastDead = -1, _lastImgLayout = -1;
        private string? _lastClass, _lastScore;
        private Color _lastNameCol = new(-1f, -1f, -1f, -1f);   // sentinel: forces first name-colour apply
        private Color _lastCrestTint = new(-1f, -1f, -1f, -1f); // sentinel: forces first crest-tint apply
        private static readonly ColorRgba MeterDeadBarRgba = new(0.35f, 0.27f, 0.27f, 1f);  // greyed bar when dead
        private ImagineCellCache _img0, _img1;

        public void Apply()
        {
            if (Bg == null || !Bg.gameObject.activeInHierarchy) return;
            var d = Data();

            ApplySelfHighlight(d);

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
            if (SpineGo != null)
            {
                var spineW = d.SpineWidth > 0f ? d.SpineWidth : WindowBuilder.MeterSpineW;
                if (!Mathf.Approximately(spineW, _lastSpineW))
                {
                    SpineGo.GetComponent<RectTransform>()!.sizeDelta = new Vector2(spineW, -4f);
                    var pad = ContentVlg.padding;
                    ContentVlg.padding = new RectOffset((int)(spineW + WindowBuilder.MeterPad), pad.right, pad.top, pad.bottom);
                    _lastSpineW = spineW;
                }
            }
            if (PrimaryGo != null && _lastPrimaryVis != (d.ShowPrimary ? 1 : 0)) { PrimaryGo.SetActive(d.ShowPrimary); _lastPrimaryVis = d.ShowPrimary ? 1 : 0; }
            var showLeader = d.IsLeader && d.ShowLeaderFlag;
            if (LeaderGo != null && _lastLeader != (showLeader ? 1 : 0)) { LeaderGo.SetActive(showLeader); _lastLeader = showLeader ? 1 : 0; }
            ApplyVoiceIcon(d);
            ApplyRowBorder(d);

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
                _lastDead = d.Dead ? 1 : 0;
            }

            // Name colour: Dead wins, then an optional NameColor override (ready-check vote),
            // else the default. Updated whenever the effective colour changes, not only on Dead.
            var effName = d.Dead ? MeterDeadName : (d.NameColor.A > 0f ? ToColor(d.NameColor) : MeterNameCol);
            if (!effName.Equals(_lastNameCol)) { Name.color = effName; _lastNameCol = effName; }

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

        // Self-row highlight: configurable colour from the meter (d.SelfAccent) rather than a fixed teal. Re-applied
        // when IsSelf flips OR the accent colour changes (live colour edit). Bg uses the colour as-is; the border
        // uses a brightened variant. A transparent (unset) accent falls back to the original framework teal so
        // rows that don't supply one (party-focus / empty slots) are unchanged.
        private void ApplySelfHighlight(in MeterRowData d)
        {
            if (_selfInit && d.IsSelf == _lastSelf && (!d.IsSelf || d.SelfAccent.Equals(_lastSelfAccent))) return;
            var hasAccent = d.SelfAccent.A > 0f;
            Bg.color = d.IsSelf ? (hasAccent ? ToColor(d.SelfAccent) : MeterSelfBg) : MeterRowBg;
            if (SelfBorder != null)
            {
                SelfBorder.SetActive(d.IsSelf);
                if (d.IsSelf)
                {
                    var edge = hasAccent ? ToColor(BrightenAccent(d.SelfAccent)) : MeterSelfBdr;
                    foreach (var img in SelfBorder.GetComponentsInChildren<Image>(true)) img.color = edge;
                }
            }
            _lastSelf = d.IsSelf; _lastSelfAccent = d.SelfAccent; _selfInit = true;
        }

        private static Color ToColor(ColorRgba c) => new(c.R, c.G, c.B, c.A);

        // Brighter variant of the self-accent for the row border, so the edge reads above the dim bg backing.
        private static ColorRgba BrightenAccent(ColorRgba c)
            => new(Mathf.Clamp01(c.R * 1.7f + 0.1f), Mathf.Clamp01(c.G * 1.7f + 0.1f), Mathf.Clamp01(c.B * 1.7f + 0.1f), 0.9f);

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

            // Optional crest tint (e.g. team-voice mic status). Alpha 0 = no tint (white).
            var tint = d.CrestTint.A > 0f ? ToColor(d.CrestTint) : Color.white;
            if (!tint.Equals(_lastCrestTint)) { Crest.color = tint; _lastCrestTint = tint; }
        }

        // Optional colored box border around the row (e.g. green while talking). Alpha 0 = hidden.
        private void ApplyRowBorder(in MeterRowData d)
        {
            if (TalkBorderGo == null || d.RowBorder.Equals(_lastRowBorder)) return;
            _lastRowBorder = d.RowBorder;
            var show = d.RowBorder.A > 0f;
            TalkBorderGo.SetActive(show);
            if (show)
            {
                var col = ToColor(d.RowBorder);
                foreach (var img in TalkBorderGo.GetComponentsInChildren<Image>(true)) img.color = col;
            }
        }

        // Name-line voice icon: plugin-supplied texture, optional tint, toggled by ShowVoiceIcon.
        private void ApplyVoiceIcon(in MeterRowData d)
        {
            if (VoiceImg == null) return;
            var tex = d.VoiceIcon as Texture;
            if (!ReferenceEquals(d.VoiceIcon, _lastVoiceTex))
            {
                VoiceImg.texture = tex;
                VoiceImg.enabled = tex != null;
                _lastVoiceTex = d.VoiceIcon;
            }
            var vtint = d.VoiceIconTint.A > 0f ? ToColor(d.VoiceIconTint) : Color.white;
            if (!vtint.Equals(_lastVoiceTint)) { VoiceImg.color = vtint; _lastVoiceTint = vtint; }
            var show = d.ShowVoiceIcon && tex != null;
            if (_lastVoiceVis != (show ? 1 : 0)) { VoiceImg.gameObject.SetActive(show); _lastVoiceVis = show ? 1 : 0; }
        }
    }
}
