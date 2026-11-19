using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// The dark readability edge of a <see cref="ThaiTmpTwin"/> whose legacy Text carries a uGUI <see cref="Outline"/>
/// (Modern bar labels' <c>LabelOutline</c>, inside-bar labels). A uGUI Outline is a mesh effect TMP never runs, and the
/// TMP SDF outline is not usable here: the twin's face material is created at runtime, so the game build ships no
/// <c>OUTLINE_ON</c> / <c>UNDERLAY_ON</c> variant of its shader (shader_feature variants exist only for materials
/// built into the player) — fw 2.34.0's first attempt set the keyword + <c>_OutlineWidth</c>/<c>_FaceDilate</c> on a
/// shared copy of the face material and drew NO outline, while the dilate (used by the base variant too) thickened
/// and softened the Thai strokes. So this does what the uGUI Outline itself does: the same text four more times in
/// the outline colour, offset by its effect distance at (±x, ±y), drawn BEHIND the twin. Copied straight from the
/// legacy Outline (colour, distance, graphic alpha), so the Thai label's edge matches the Latin one.
/// <para>Cost: four extra TMP texts per outlined Thai label, built on the first Thai string only; per string change
/// four SetText calls; per apply a colour/size compare (writes only on change). The face is the twin's own, so the
/// main text stays exactly the meter-row twin (no material change → as crisp as a CombatMeter Thai name).</para>
/// </summary>
internal sealed class ThaiTmpHalo
{
    private readonly IStyledTextHandle[] _copies;
    private readonly Outline _src;
    private Color _color;
    private int _size;
    private bool _on;

    private ThaiTmpHalo(IStyledTextHandle[] copies, Outline src, int size)
    {
        _copies = copies; _src = src; _size = size; _color = new Color(-1f, 0f, 0f, 0f);
    }

    /// <summary>Four offset copies of <paramref name="s"/> as siblings just before <paramref name="twinGo"/> (so they
    /// draw under it), sharing its rect. Null when the legacy Text has no Outline or a copy can't be built.</summary>
    public static ThaiTmpHalo? Create(Text legacy, GameObject twinGo, string s, StyledTextSpec spec)
    {
        var create = StyledTextFactory.CreateBold;
        var ol = legacy.GetComponent<Outline>();
        if (create == null || ol == null) return null;
        var parent = twinGo.transform.parent;
        var d = ol.effectDistance;
        var copies = new IStyledTextHandle[4];
        for (int i = 0; i < 4; i++)
        {
            var h = create(parent, new StyledTextSpec(s, spec.FontSize, Color.black, spec.Wrap) { Bold = spec.Bold, Align = spec.Align });
            var go = h?.Go;
            if (h == null || go == null) { Destroy(copies); return null; }
            copies[i] = h;
            go.name = "ThaiTextEdge";
            go.transform.SetSiblingIndex(twinGo.transform.GetSiblingIndex());   // before the twin → drawn under it
            var rt = go.GetComponent<RectTransform>();
            var src = twinGo.GetComponent<RectTransform>();
            rt.anchorMin = src.anchorMin; rt.anchorMax = src.anchorMax; rt.pivot = src.pivot; rt.sizeDelta = src.sizeDelta;
            // uGUI Outline's four offsets: (x, y), (x, −y), (−x, y), (−x, −y).
            rt.anchoredPosition = src.anchoredPosition + new Vector2(i < 2 ? d.x : -d.x, (i & 1) == 0 ? d.y : -d.y);
            go.AddComponent<LayoutElement>().ignoreLayout = true;   // decoration: never a layout slot
            go.SetActive(false);
        }
        return new ThaiTmpHalo(copies, ol, spec.FontSize);
    }

    /// <summary>New string (Thai, shown on the twin).</summary>
    public void SetText(string s)
    {
        foreach (var h in _copies) h.SetText(s);
    }

    /// <summary>Show with the twin (and only while the legacy Outline is enabled), or hide.</summary>
    public void Show(bool twinShowing)
    {
        bool on = twinShowing && _src != null && _src.enabled;
        if (on == _on) return;
        _on = on;
        foreach (var h in _copies) h.Go?.SetActive(on);
    }

    /// <summary>Per apply while the twin shows: follow the label's alpha (useGraphicAlpha) and size, and the Outline's
    /// enabled state. Writes only on change.</summary>
    public void Sync(Color text, int size)
    {
        Show(true);
        if (!_on) return;
        var c = _src.effectColor;
        if (_src.useGraphicAlpha) c.a *= text.a;
        if (c != _color) { _color = c; foreach (var h in _copies) h.SetColor(c); }
        if (size != _size) { _size = size; foreach (var h in _copies) h.SetFontSize(size); }
    }

    private static void Destroy(IStyledTextHandle?[] copies)
    {
        foreach (var h in copies)
            if (h?.Go is { } go) UnityEngine.Object.Destroy(go);
    }
}
