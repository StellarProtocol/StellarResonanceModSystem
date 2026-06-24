using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// PanelElement rendering: a themed popup container (border + opaque lifted background + padded content host).
// Split out of WindowBuilder.cs to keep that file under the LoC gate.
internal sealed partial class WindowBuilder
{
    // Themed panel container (PanelElement): a border-coloured fill with a 2px-inset background fill, plus a
    // padded content host for the single child. Both fills raycast so the panel blocks click-through.
    private void BuildPanel(PanelElement p, Transform parent, WindowToken token)
    {
        // The panel is a NORMAL layout child: its VLG reports a preferred height (Child + padding) so a
        // content-sized parent (e.g. a Borderless popup window) measures it and grows to fit. The border and
        // background are ignoreLayout Images that stretch over the panel's final rect — decorative, so they
        // never drive size. (Marking the panel itself ignoreLayout collapses a content-sized window to nothing.)
        var panel = UGuiPrimitives.NewChild("Panel", parent);
        var vlg = panel.AddComponent<VerticalLayoutGroup>();
        var pad = Mathf.RoundToInt(p.Padding);
        vlg.padding = new RectOffset(pad, pad, pad, pad);
        vlg.spacing = 2f;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperLeft;

        var border = UGuiPrimitives.NewChild("Border", panel.transform);
        UGuiPrimitives.Stretch(border);
        border.AddComponent<LayoutElement>().ignoreLayout = true;
        var bimg = border.AddComponent<Image>(); bimg.color = PanelBorderColor(); bimg.raycastTarget = true;
        token.ReskinActions.Add(() => { if (bimg != null) bimg.color = PanelBorderColor(); });

        var bgGo = UGuiPrimitives.NewChild("Bg", panel.transform);
        UGuiPrimitives.Stretch(bgGo);
        bgGo.AddComponent<LayoutElement>().ignoreLayout = true;
        var brt = bgGo.GetComponent<RectTransform>();
        // 2px inset so the border Image reads as a crisp frame around the body (the raw 1px theme border is
        // near-invisible — a popup needs a drawn edge to look like a panel, not floating text).
        brt.offsetMin = new Vector2(2f, 2f); brt.offsetMax = new Vector2(-2f, -2f);
        var bg = bgGo.AddComponent<Image>(); bg.color = PanelBgColor(); bg.raycastTarget = true;
        token.ReskinActions.Add(() => { if (bg != null) bg.color = PanelBgColor(); });

        // Child added LAST so it renders on top of the border/background siblings.
        BuildElement(p.Child, panel.transform, token);
    }

    // Panel body: the theme menu background, forced opaque and lifted ~6% toward white so a dark popup separates
    // from the dark content behind it instead of blending in.
    private Color PanelBgColor()
    {
        var c = _assets.MenuBackground;
        return new Color(Mathf.Lerp(c.r, 1f, 0.06f), Mathf.Lerp(c.g, 1f, 0.06f), Mathf.Lerp(c.b, 1f, 0.06f), 1f);
    }

    // Panel frame: the menu background hue darkened to a solid edge — a recessed border that frames the popup
    // without the gold accent reading as a button. (The raw MenuBorder token sits at ~0.15 alpha, invisible.)
    private Color PanelBorderColor()
    {
        var c = _assets.MenuBackground;
        return new Color(c.r * 0.65f, c.g * 0.65f, c.b * 0.65f, 1f);
    }
}
