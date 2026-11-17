using System;
using Stellar.Abstractions.Domain;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// BarElement's poll-diff binding, split out of WindowBuilder.Bindings.cs (500-LoC file gate). Pure relocation.
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
        // Per-refresh fill colour (BarElement.FillColorFn). FillGraphic/FillColorFn left null → the fill keeps its
        // build-time colour (the static BarElement.Fill, painted once at build) with no override machinery — a bar
        // that doesn't set FillColorFn is byte-for-byte unchanged. Mirrors TextBinding.ColorFn: Override every poll a
        // colour is returned (so a reskin never wins), RestoreDefault once it goes back to null. The restore-default
        // is the fill's build-time colour captured on the first poll (= the static Fill), matching how TextBinding
        // restores its build colour when DefaultColor is null.
        public Graphic? FillGraphic; public Func<ColorRgba?>? FillColorFn;
        private ColorOverrideTracker _fillColor; private Color? _fillBuildColor;
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
                if (s != _lastLabel) { Label.text = s; if (LabelShadow != null) LabelShadow.text = UGuiPrimitives.StripColorTags(s); _lastLabel = s; }
            }
            if (FillGraphic != null && FillColorFn != null) ApplyFillColor(FillColorFn());
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
