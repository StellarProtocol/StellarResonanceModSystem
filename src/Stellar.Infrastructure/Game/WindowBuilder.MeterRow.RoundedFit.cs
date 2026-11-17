using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Rounded meter row: top-line overflow fit. The top line is one HorizontalLayoutGroup
//   [crest][skull][rank][flag][name][class][· spec][score][<flex spacer>][voice][share %][imagines]
// whose Texts are built with horizontalOverflow = Overflow and a min width of 0 (uGUI Text's ILayoutElement). When the
// line's summed preferred width exceeds its rect (narrow rows: the 20-player Party grid at ~270 px with the debuff block
// reserving the right edge) the HLG SQUEEZES every child proportionally between min (0) and preferred — the rects shrink
// but the glyphs keep drawing at full width (Overflow), so "· Stormblade" spilled into the voice icon and the right-
// aligned share % spilled left into it (in-game 2026-11-17). Nothing ever collapsed: CombatMeter's width collapse
// (<230 spec / <180 share) only runs in List mode and measures the WINDOW, not this line.
//
// Fix: keep the line's preferred sum ≤ its width so the HLG never squeezes. The spec drops first (hidden + out of
// layout), then the class label (CombatMeter's "Both" shows class AND spec), then the name is cut with the framework's own prefix + "..." (UGuiPrimitives.EllipsizeFit — NOT TMP/uGUI
// Ellipsis, see KB) and its LayoutElement pins the slot. Decision rule = MeterTopLineFit (unit-tested).
//
// Cost: steady state = one rect read + one HLG preferredWidth read + reference compares per poll, no allocation. A re-fit
// (sum the other children's preferred widths, measure, cut) runs only when the line width, the line's laid-out preferred
// sum (share text / voice / imagines / rank changed), the name, the class, the spec or a font reskin changes. A re-fit changes the
// sum it keys on, so the next poll re-fits once more with identical results and then settles.
internal sealed partial class WindowBuilder
{
    // Hangs the fit's handles on the skin (build time).
    private static void AttachTopLineFit(MeterRowRoundedSkin skin, MeterRowBinding b)
    {
        if (b.TopLine == null || b.Name == null || b.Spec == null) return;
        skin.Line = b.TopLine.TryCast<RectTransform>();
        skin.LineHlg = b.TopLine.GetComponent<HorizontalLayoutGroup>();
        skin.Name = b.Name;
        skin.NameLe = b.Name.GetComponent<LayoutElement>();
        skin.SpecLe = b.Spec.GetComponent<LayoutElement>();
        skin.ClassLabel = b.ClassName;
        skin.ClassLe = b.ClassName?.GetComponent<LayoutElement>();
    }

    internal sealed partial class MeterRowRoundedSkin
    {
        public RectTransform? Line;
        public HorizontalLayoutGroup? LineHlg;
        public Text? Name, ClassLabel;
        public LayoutElement? NameLe, SpecLe, ClassLe;

        private float _fitLineW = -1f, _fitTotal = -1f, _nameFullW, _specFullW, _classFullW;
        private string? _fitName, _fitSpec, _fitClass;
        private int _fitSpecWanted = -1, _fitClassWanted = -1;
        private bool _fitMeasure = true;   // full widths need (re)measuring: first fit, or a font / Font Scale reskin

        /// <summary>Call after the binding has written this poll's name/spec text.</summary>
        public void FitTopLine(in MeterRowData d)
        {
            if (Line == null || LineHlg == null || Name == null || NameLe == null || SpecLe == null) return;
            float lineW = Line.rect.width;
            if (lineW <= 1f) return;   // not laid out yet
            float total = LineHlg.preferredWidth;
            var name = d.Name ?? "";
            bool specWanted = d.ShowSpec && !string.IsNullOrEmpty(d.Spec);
            bool nameChanged = !ReferenceEquals(name, _fitName) && name != _fitName;
            bool specChanged = specWanted && !ReferenceEquals(d.Spec, _fitSpec) && d.Spec != _fitSpec;
            bool classWanted = ClassLabel != null && ClassLe != null && d.ShowClassName && !string.IsNullOrEmpty(d.ClassName);
            bool classChanged = classWanted && !ReferenceEquals(d.ClassName, _fitClass) && d.ClassName != _fitClass;
            int sw = specWanted ? 1 : 0, cw = classWanted ? 1 : 0;
            if (!_fitMeasure && !nameChanged && !specChanged && !classChanged && sw == _fitSpecWanted && cw == _fitClassWanted
                && Mathf.Approximately(lineW, _fitLineW) && Mathf.Approximately(total, _fitTotal)) return;

            if (_fitMeasure || nameChanged)
            {
                Name.text = name;   // the slot may hold a previous cut; measure the full string
                _nameFullW = Name.preferredWidth;
            }
            if (specWanted && (_fitMeasure || specChanged)) _specFullW = Spec.preferredWidth;   // binding set "· Spec"
            if (classWanted && (_fitMeasure || classChanged)) _classFullW = ClassLabel!.preferredWidth;   // binding set it
            _fitMeasure = false;
            _fitName = name; _fitSpec = specWanted ? d.Spec : null; _fitSpecWanted = sw;
            _fitClass = classWanted ? d.ClassName : null; _fitClassWanted = cw;
            _fitLineW = lineW; _fitTotal = total;

            float spacing = LineHlg.spacing;
            var (showClass, showSpec, nameW) = MeterTopLineFit.Decide(lineW - OthersWidth(spacing), _nameFullW,
                new MeterTopLineFit.Label(classWanted, _classFullW), new MeterTopLineFit.Label(specWanted, _specFullW), spacing);

            if (specWanted)
            {
                if (Spec.enabled != showSpec) Spec.enabled = showSpec;
                if (SpecLe.ignoreLayout == showSpec) SpecLe.ignoreLayout = !showSpec;   // dropped spec takes no width or gap
            }
            // Same hide as the spec: Text.enabled + ignoreLayout, never SetActive — the binding owns the GameObject's
            // active state (its _lastClassVis cache would go stale and never re-show the label).
            if (classWanted)
            {
                if (ClassLabel!.enabled != showClass) ClassLabel.enabled = showClass;
                if (ClassLe!.ignoreLayout == showClass) ClassLe.ignoreLayout = !showClass;
            }
            if (nameW < _nameFullW) Name.text = UGuiPrimitives.EllipsizeFit(Name, name, nameW);
            else if (Name.text != name) Name.text = name;
            if (!Mathf.Approximately(NameLe.preferredWidth, nameW)) NameLe.preferredWidth = nameW;
        }

        // Padding + every laid-out child except name, class and spec + one gap per such child (the gaps between them plus the
        // name's own gap). Live preferred widths (LayoutUtility), so a share text changed this poll is already counted.
        private float OthersWidth(float spacing)
        {
            var hlg = LineHlg!; var line = Line!;
            var pad = hlg.padding;
            float w = pad.left + pad.right;
            var nameT = Name!.transform; var specT = Spec.transform;
            var classT = ClassLabel != null ? ClassLabel.transform : null;
            for (int i = 0; i < line.childCount; i++)
            {
                var c = line.GetChild(i);
                if (!c.gameObject.activeSelf || c == nameT || c == specT || c == classT) continue;
                var le = c.GetComponent<LayoutElement>();
                if (le != null && le.ignoreLayout) continue;
                var rt = c.TryCast<RectTransform>();
                if (rt == null) continue;
                w += LayoutUtility.GetPreferredWidth(rt) + spacing;
            }
            return w;
        }
    }
}
