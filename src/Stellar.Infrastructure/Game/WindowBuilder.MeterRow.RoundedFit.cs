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
        private ThaiTmpTwin? _fitTwin;   // the Thai twin that was the name slot at the last fit (null = the legacy Text)
        private Transform? _twinT;       // _fitTwin's transform + LayoutElement, cached per swap
        private LayoutElement? _twinLe;
        // The spec / class Thai twins that were showing at the last fit (fw #169), + their transforms (OthersWidth skip),
        // cached per swap like the name's.
        private ThaiTmpTwin? _fitSpecTwin, _fitClassTwin;
        private Transform? _specTwinT, _classTwinT;

        /// <summary>Call after the binding has written this poll's name/spec text. <paramref name="thai"/> = the name's Thai TMP twin
        /// (WindowBuilder.MeterRowBinding.Name.cs): while it shows, IT is the name slot — measured by its own face, cut by
        /// TMP (Ellipsis twin, re-cut to its rect by Sync), its LayoutElement pinned here — and the hidden legacy Text is left alone.</summary>
        public void FitTopLine(in MeterRowData d, ThaiTmpTwin? thai, ThaiTmpTwin? specThai, ThaiTmpTwin? classThai)
        {
            if (Line == null || LineHlg == null || Name == null || NameLe == null || SpecLe == null) return;
            float lineW = Line.rect.width;
            if (lineW <= 1f) return;   // not laid out yet
            var twin = thai is { Showing: true } ? thai : null;   // field reads only: the steady-state poll stays wrapper-free
            var specTwin = specThai is { Showing: true } ? specThai : null;
            var classTwin = classThai is { Showing: true } ? classThai : null;
            float total = LineHlg.preferredWidth;
            var name = d.Name ?? "";
            bool specWanted = d.ShowSpec && !string.IsNullOrEmpty(d.Spec);
            bool nameChanged = !ReferenceEquals(name, _fitName) && name != _fitName;
            bool specChanged = specWanted && !ReferenceEquals(d.Spec, _fitSpec) && d.Spec != _fitSpec;
            bool classWanted = ClassLabel != null && ClassLe != null && d.ShowClassName && !string.IsNullOrEmpty(d.ClassName);
            bool classChanged = classWanted && !ReferenceEquals(d.ClassName, _fitClass) && d.ClassName != _fitClass;
            int sw = specWanted ? 1 : 0, cw = classWanted ? 1 : 0;
            if (!_fitMeasure && !nameChanged && !specChanged && !classChanged && sw == _fitSpecWanted && cw == _fitClassWanted
                && twin == _fitTwin && specTwin == _fitSpecTwin && classTwin == _fitClassTwin
                && Mathf.Approximately(lineW, _fitLineW) && Mathf.Approximately(total, _fitTotal)) return;

            if (twin != null) _nameFullW = twin.PaddedNaturalWidth;   // cached by the handle: no TMP re-measure here
            else if (_fitMeasure || nameChanged)
            {
                LegacyText.SetRouted(Name, name);   // the slot may hold a previous cut; measure the full string
                _nameFullW = Name.preferredWidth;
            }
            // Binding set "· Spec" / the class; a showing Thai twin reports its own (padded) natural width.
            if (specWanted && (_fitMeasure || specChanged || specTwin != _fitSpecTwin))
                _specFullW = specTwin != null ? specTwin.PreferredWidth : Spec.preferredWidth;
            if (classWanted && (_fitMeasure || classChanged || classTwin != _fitClassTwin))
                _classFullW = classTwin != null ? classTwin.PreferredWidth : ClassLabel!.preferredWidth;
            _fitMeasure = false;
            _fitName = name; _fitSpec = specWanted ? d.Spec : null; _fitSpecWanted = sw;
            _fitClass = classWanted ? d.ClassName : null; _fitClassWanted = cw;
            _fitLineW = lineW; _fitTotal = total;
            if (twin != _fitTwin) CacheTwinSlot(twin);
            if (specTwin != _fitSpecTwin) { _fitSpecTwin = specTwin; _specTwinT = specTwin?.Go is { } sg ? sg.transform : null; }
            if (classTwin != _fitClassTwin) { _fitClassTwin = classTwin; _classTwinT = classTwin?.Go is { } cg ? cg.transform : null; }

            float spacing = LineHlg.spacing;
            var (showClass, showSpec, nameW) = MeterTopLineFit.Decide(lineW - OthersWidth(spacing, _twinT), _nameFullW,
                new MeterTopLineFit.Label(classWanted, _classFullW), new MeterTopLineFit.Label(specWanted, _specFullW), spacing);

            // Drop = Text.enabled + ignoreLayout (a dropped label takes no width or gap), never SetActive — the binding owns
            // the GameObject's active state (its _lastClassVis cache would go stale and never re-show the label). With a
            // Thai twin the same drop lands on whichever element is current (ThaiTmpTwin.SetDrawn).
            if (specWanted) Drop(Spec, SpecLe, specThai, showSpec);
            if (classWanted) Drop(ClassLabel!, ClassLe!, classThai, showClass);
            SetNameSlot(name, nameW, twin != null);
        }

        private static void Drop(Text legacy, LayoutElement le, ThaiTmpTwin? twin, bool show)
        {
            if (twin != null) { twin.SetDrawn(show); return; }
            if (legacy.enabled != show) legacy.enabled = show;
            if (le.ignoreLayout == show) le.ignoreLayout = !show;
        }

        // Re-fit path only: the twin's slot (its GameObject + the LayoutElement copied from the legacy name's) is looked up
        // once per twin swap, not per poll (each IL2CPP GetComponent hands back a fresh wrapper).
        private void CacheTwinSlot(ThaiTmpTwin? twin)
        {
            _fitTwin = twin;
            var go = twin?.Go;
            _twinT = go == null ? null : go.transform;
            _twinLe = go == null ? null : go.GetComponent<LayoutElement>();
        }

        // Legacy name: cut with the framework's own prefix + "..." and pin the slot. Thai twin: pin ITS slot only — TMP cuts
        // the twin to its laid-out rect (Sync → FitToRect, the next poll once the HLG has applied this width).
        private void SetNameSlot(string name, float nameW, bool twin)
        {
            if (twin)
            {
                if (_twinLe != null && !Mathf.Approximately(_twinLe.preferredWidth, nameW)) _twinLe.preferredWidth = nameW;
                return;
            }
            if (nameW < _nameFullW) LegacyText.SetRouted(Name!, UGuiPrimitives.EllipsizeFit(Name!, name, nameW));
            else if (Name!.text != name) LegacyText.SetRouted(Name, name);
            if (!Mathf.Approximately(NameLe!.preferredWidth, nameW)) NameLe.preferredWidth = nameW;
        }

        // Padding + every laid-out child except name, class and spec + one gap per such child (the gaps between them plus the
        // name's own gap). Live preferred widths (LayoutUtility), so a share text changed this poll is already counted.
        private float OthersWidth(float spacing, Transform? twinT)
        {
            var hlg = LineHlg!; var line = Line!;
            var pad = hlg.padding;
            float w = pad.left + pad.right;
            var nameT = Name!.transform; var specT = Spec.transform;
            var classT = ClassLabel != null ? ClassLabel.transform : null;
            for (int i = 0; i < line.childCount; i++)
            {
                var c = line.GetChild(i);
                if (!c.gameObject.activeSelf || c == nameT || c == specT || c == classT || c == twinT
                    || c == _specTwinT || c == _classTwinT) continue;
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
