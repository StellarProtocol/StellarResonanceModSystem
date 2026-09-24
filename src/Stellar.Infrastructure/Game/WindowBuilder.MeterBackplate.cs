using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Backplate construction for the bespoke CombatMeter row — split out of WindowBuilder.MeterRow.cs (which
// crossed the 500-LoC file gate when the shield overlay was added). Pure relocation: this is a partial of the
// same WindowBuilder class, so it reads the meter colour constants / MeterSpineW / AddStretchedImage that stay
// in MeterRow.cs. Builds the ignore-layout bg + self/talk borders + HP spine (+ the shield overlay band).
internal sealed partial class WindowBuilder
{
    // Bg (self-backing) + self-highlight border + talk border + HP spine (+ shield overlay) — the ignore-layout backplate.
    private (Image bg, GameObject border, GameObject talkBorder, Image spineFill, Image shieldFill, GameObject spine) BuildMeterBackplate(Transform row)
    {
        var bg = AddStretchedImage(row, "Bg", MeterRowBg, ignoreLayout: true);

        var border = BuildBorder(row, "SelfBorder");
        // Parallel 4-edge border the plugin tints (e.g. green while talking); hidden until RowBorder alpha > 0.
        var talkBorder = BuildBorder(row, "TalkBorder");
        talkBorder.SetActive(false);

        var spine = UGuiPrimitives.NewChild("Spine", row);
        spine.AddComponent<LayoutElement>().ignoreLayout = true;
        var srt = spine.GetComponent<RectTransform>();
        srt.anchorMin = new Vector2(0f, 0f); srt.anchorMax = new Vector2(0f, 1f); srt.pivot = new Vector2(0f, 0.5f);
        srt.sizeDelta = new Vector2(MeterSpineW, -4f); srt.anchoredPosition = new Vector2(2f, 0f); // inset past the left border
        var spineBg = spine.AddComponent<Image>(); spineBg.color = MeterSpineBg; spineBg.raycastTarget = false;
        // HP fill: a bottom-anchored solid rect whose HEIGHT is the HP fraction (the binding drives anchorMax.y).
        // NOT Image.Type.Filled — a uGUI Image with no sprite ignores fillAmount and draws a FULL quad, so the
        // migrated Filled spine stayed full regardless of HP. Anchor-resize needs no sprite and mirrors how the
        // role bar clips its width. Build-default colour is transparent: the binding paints the real HP colour
        // only once it differs from the struct default, so an empty placeholder row keeps an invisible spine.
        var spineFill = AddSpineFill(spine.transform, "Fill", Color.clear);
        // Shield overlay: grey/white band drawn OVER the green HP fill (later sibling → on top), same bottom-
        // anchored anchorMax.y mechanism (the binding drives it = HpShieldFraction). Built inactive: a shield-
        // less row never shows it (byte-identical to before), the binding activates it only when the fraction > 0.
        var shieldFill = AddSpineFill(spine.transform, "Shield", MeterSpineShield);
        shieldFill.gameObject.SetActive(false);
        return (bg, border, talkBorder, spineFill, shieldFill, spine);
    }

    // A bottom-anchored, full-width fill inside the spine cell whose HEIGHT the binding drives via anchorMax.y.
    // Shared by the HP fill and the shield overlay (identical geometry) — see BuildMeterBackplate's HP-fill note.
    private static Image AddSpineFill(Transform spine, string name, Color color)
    {
        var go = UGuiPrimitives.NewChild(name, spine);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f); rt.pivot = new Vector2(0.5f, 0f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        var img = go.AddComponent<Image>(); img.color = color; img.raycastTarget = false;
        return img;
    }

    // A 4-edge (1-px) box border filling its parent. Edge colour is set at apply time by the binding.
    private static GameObject BuildBorder(Transform row, string name)
    {
        var border = UGuiPrimitives.NewChild(name, row);
        border.AddComponent<LayoutElement>().ignoreLayout = true;
        UGuiPrimitives.Stretch(border);
        AddEdge(border.transform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));   // top    (horizontal)
        AddEdge(border.transform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(0f, 1f));   // bottom (horizontal)
        AddEdge(border.transform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 0f));   // left   (vertical)
        AddEdge(border.transform, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(1f, 0f));   // right  (vertical)
        return border;
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
