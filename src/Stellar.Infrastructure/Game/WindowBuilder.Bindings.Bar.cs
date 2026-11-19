using System;
using Stellar.Abstractions.Domain;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// BarElement's poll-diff binding, split out of WindowBuilder.Bindings.cs (500-LoC file gate).
internal sealed partial class WindowBuilder
{
    internal sealed class BarBinding : GatedBinding
    {
        // Width via the clip rect's right anchor, NOT fillAmount (spriteless Image ignores it) — see BuildBarTrack.
        // For a HudOverlay Default bar FillRect is left null (the fill is an Image.Type.Filled smoothed by a pulse,
        // not an anchor-clip) so this binding then carries only the label + its shadow twin.
        public RectTransform FillRect = null!;
        public Func<float> Fraction = null!;
        // Label + (HudOverlay only) its offset-twin shadow. LabelShadow null on the Menu path → unchanged.
        public Text? Label; public Text? LabelShadow; public Func<string>? LabelFn;
        // Menu-path label (no shadow twin): a Thai string → the TMP twin (ThaiTmpTwin; outlined for an inside label).
        public bool ThaiTmp; private ThaiTmpTwin? _thai;
        // Per-refresh fill colour (BarElement.FillColorFn). FillGraphic/FillColorFn left null → the fill keeps its
        // build-time colour (the static BarElement.Fill, painted once at build) with no override machinery — a bar
        // that doesn't set FillColorFn is byte-for-byte unchanged. Mirrors TextBinding.ColorFn: Override every poll a
        // colour is returned (so a reskin never wins), RestoreDefault once it goes back to null. The restore-default
        // is the fill's build-time colour captured on the first poll (= the static Fill), matching how TextBinding
        // restores its build colour when DefaultColor is null.
        public Graphic? FillGraphic; public Func<ColorRgba?>? FillColorFn;
        private ColorOverrideTracker _fillColor; private Color? _fillBuildColor;
        private float _lastFrac = -1f; private string? _lastLabel;
        // 2.34.0 segment opt-ins (null = the classic [0, Fraction] fill, unchanged): Start drives anchorMin.x (an overlay
        // segment starting after the HP fill, or the glow behind it); HideWhenEmpty is switched off while the segment has
        // no width (a glow ring would otherwise draw a blob around a zero-width segment). Written only on change.
        public Func<float>? Start; public Behaviour? HideWhenEmpty;
        private float _lastStart = -1f; private int _lastShown = -1;
        public void Apply()
        {
            if (FillRect != null && !FillRect.gameObject.activeInHierarchy) return;
            if (FillRect != null)
            {
                var f = Mathf.Clamp01(Fraction());
                if (Start != null)
                {
                    var s = Mathf.Clamp01(Start());
                    if (f < s) f = s;
                    if (!Mathf.Approximately(s, _lastStart)) { FillRect.anchorMin = new Vector2(s, 0f); _lastStart = s; }
                    if (HideWhenEmpty != null) ApplyShown(f - s > 0.0005f);
                }
                else if (HideWhenEmpty != null) ApplyShown(f > 0.0005f);
                if (!Mathf.Approximately(f, _lastFrac)) { FillRect.anchorMax = new Vector2(f, 1f); _lastFrac = f; }
            }
            if (Label != null && LabelFn != null)
            {
                var s = LabelFn();
                if (s != _lastLabel)
                {
                    _lastLabel = s;
                    // Menu path: a Thai label shows on the TMP twin (shipped Thai face); anything else on the legacy Text.
                    if (!(ThaiTmp && ThaiTmpTwin.Route(ref _thai, Label, s)))
                    {
                        if (ThaiTmp && _thai == null && GlyphScript.IsThai(s)) ThaiTmp = false;   // unbuildable: stop retrying
                        Label.text = s;
                        if (LabelShadow != null) LabelShadow.text = UGuiPrimitives.StripColorTags(s);
                    }
                }
                else if (_thai is { Showing: true }) _thai.Sync();   // mirror a reskin's size onto the twin
            }
            if (FillGraphic != null && FillColorFn != null) ApplyFillColor(FillColorFn());
            if (TrackGraphic != null && TrackColorFn != null) ApplyTrackColor(TrackColorFn());
        }

        // Per-refresh track colour (BarElement.TrackColorFn, 2.33.0). Left null → no machinery, unchanged. Painted only
        // when the returned colour changes (no per-poll write); null restores the build-time track colour once.
        public Graphic? TrackGraphic; public Func<ColorRgba?>? TrackColorFn;
        private Color? _trackBuildColor; private ColorRgba? _lastTrack; private bool _trackInit;
        private void ApplyTrackColor(ColorRgba? v)
        {
            if (_trackInit && Nullable.Equals(v, _lastTrack)) return;
            _trackBuildColor ??= TrackGraphic!.color;
            TrackGraphic!.color = v is { } c ? new Color(c.R, c.G, c.B, c.A) : _trackBuildColor.Value;
            _lastTrack = v; _trackInit = true;
        }

        private void ApplyShown(bool shown)
        {
            var v = shown ? 1 : 0;
            if (v == _lastShown) return;
            HideWhenEmpty!.enabled = shown; _lastShown = v;
        }

        private void ApplyFillColor(ColorRgba? v)
        {
            _fillBuildColor ??= FillGraphic!.color;   // the static Fill painted at build = the restore-default
            switch (_fillColor.Next(v.HasValue))
            {
                case ColorStep.Override: FillGraphic!.color = new Color(v!.Value.R, v.Value.G, v.Value.B, v.Value.A); break;
                case ColorStep.RestoreDefault: FillGraphic!.color = _fillBuildColor.Value; break;
            }
        }
    }
}
