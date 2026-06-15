using Stellar.Abstractions.Domain;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>WindowBuilder overlay chromes — Tracker, Party, PillStatus. Read-only HUD-style frames built
/// now (their plugin windows migrate in Plan 5/6); exercised by sandbox stories + golden-diffed against the
/// IMGUI captures. Reuse the baked PanelBg (translucent dark body), HGradient (mint→transparent divider),
/// Capsule (mint dot), and FrameBg (pill) sprites from <see cref="WindowThemeAssets"/>.</summary>
internal sealed partial class WindowBuilder
{
    // Tracker: translucent dark body, a title row (mint dot + bold title), a mint→transparent gradient
    // divider, then the read-only content. No close. Drag handle = title row (wired in Plan 4).
    private (RectTransform root, Transform content) BuildTrackerChrome(WindowSpec spec, Transform parent)
    {
        var root = NewOverlayRoot(spec, parent, _assets.PanelBg);
        var title = UGuiPrimitives.NewChild("TrackerTitle", root.transform);
        var hlg = title.AddComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(12, 12, 9, 9); hlg.spacing = 8f;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true; hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false;
        var dot = UGuiPrimitives.NewChild("Dot", title.transform);
        var dle = dot.AddComponent<LayoutElement>(); dle.preferredWidth = 9f; dle.preferredHeight = 9f;
        var dimg = dot.AddComponent<Image>(); dimg.sprite = _assets.Capsule; dimg.type = Image.Type.Sliced;
        dimg.color = _assets.MenuAccent; dimg.raycastTarget = false;
        var tGo = UGuiPrimitives.NewChild("Title", title.transform);
        var t = tGo.AddComponent<Text>(); UGuiPrimitives.ConfigureText(t, 13, TextAnchor.MiddleLeft, bold: true);
        t.color = _assets.MenuText; t.text = spec.Title; t.raycastTarget = false;

        AddHGradientDivider(root.transform);
        var content = AddContentContainer(root.transform);
        return (root, content);
    }

    // Party: translucent dark body, a left accent edge bar, a mint banner tab (bold dark title),
    // a gradient divider, then read-only roster content.
    private (RectTransform root, Transform content) BuildPartyChrome(WindowSpec spec, Transform parent)
    {
        var root = NewOverlayRoot(spec, parent, _assets.PanelBg);
        // Left accent edge (ignore-layout overlay, 3 px, full height).
        var edge = UGuiPrimitives.NewChild("Edge", root.transform);
        edge.AddComponent<LayoutElement>().ignoreLayout = true;
        var ert = edge.GetComponent<RectTransform>();
        ert.anchorMin = new Vector2(0f, 0f); ert.anchorMax = new Vector2(0f, 1f);
        ert.sizeDelta = new Vector2(3f, 0f); ert.anchoredPosition = new Vector2(1.5f, 0f);
        var eimg = edge.AddComponent<Image>(); eimg.color = _assets.MenuAccent; eimg.raycastTarget = false;

        var banner = UGuiPrimitives.NewChild("Banner", root.transform);
        var ble = banner.AddComponent<LayoutElement>(); ble.minHeight = 26f; ble.preferredHeight = 26f;
        var blg = banner.AddComponent<HorizontalLayoutGroup>();
        blg.padding = new RectOffset(14, 16, 5, 5); blg.childAlignment = TextAnchor.MiddleLeft;
        blg.childControlWidth = true; blg.childControlHeight = true;
        blg.childForceExpandWidth = false; blg.childForceExpandHeight = false;
        // The banner tab background hugs the title (an ignore-layout tinted strip behind it).
        var bbg = UGuiPrimitives.NewChild("BannerBg", banner.transform);
        bbg.AddComponent<LayoutElement>().ignoreLayout = true; UGuiPrimitives.Stretch(bbg);
        var bbgImg = bbg.AddComponent<Image>(); bbgImg.color = _assets.MenuAccent; bbgImg.raycastTarget = true;
        var tGo = UGuiPrimitives.NewChild("Title", banner.transform);
        var t = tGo.AddComponent<Text>(); UGuiPrimitives.ConfigureText(t, 12, TextAnchor.MiddleLeft, bold: true);
        t.color = new Color(0.06f, 0.16f, 0.15f, 1f); t.text = spec.Title; t.raycastTarget = false;

        AddHGradientDivider(root.transform);
        var content = AddContentContainer(root.transform);
        return (root, content);
    }

    // PillStatus: a single rounded chip (accent-bordered FrameBg), 6/12 padding, single-line content.
    // Whole chip is the drag handle (raycastTarget). No titlebar, no close.
    private (RectTransform root, Transform content) BuildPillChrome(WindowSpec spec, Transform parent)
    {
        var root = UGuiPrimitives.NewRect(spec.Id, parent);
        var img = root.gameObject.AddComponent<Image>();
        img.sprite = _assets.FrameBg; img.type = Image.Type.Sliced; img.raycastTarget = true;
        var hlg = root.gameObject.AddComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(12, 12, 6, 6); hlg.spacing = 7f;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true; hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false;
        var fitter = root.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return (root, root.transform);
    }

    // ---- shared overlay-chrome helpers ----

    private RectTransform NewOverlayRoot(WindowSpec spec, Transform parent, Sprite? bodySprite)
    {
        var root = UGuiPrimitives.NewRect(spec.Id, parent);
        var le = root.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = spec.DefaultRect.Width > 0 ? spec.DefaultRect.Width : 280f;
        var fitter = root.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var vlg = root.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 0f; vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        if (bodySprite != null)
        {
            var img = root.gameObject.AddComponent<Image>();
            img.sprite = bodySprite; img.type = Image.Type.Sliced; img.raycastTarget = true;
        }
        return root;
    }

    private Transform AddContentContainer(Transform parent)
    {
        var content = UGuiPrimitives.NewChild("Content", parent);
        var clg = content.AddComponent<VerticalLayoutGroup>();
        clg.padding = new RectOffset(12, 12, 8, 12); clg.spacing = RowGap;
        clg.childControlWidth = true; clg.childControlHeight = true;
        clg.childForceExpandWidth = true; clg.childForceExpandHeight = false;
        clg.childAlignment = TextAnchor.UpperLeft;
        return content.transform;
    }

    private void AddHGradientDivider(Transform parent)
    {
        var go = UGuiPrimitives.NewChild("GradDivider", parent);
        var le = go.AddComponent<LayoutElement>(); le.minHeight = 2f; le.preferredHeight = 2f; le.flexibleWidth = 1f;
        var raw = go.AddComponent<RawImage>();
        if (_assets.HGradient != null) raw.texture = _assets.HGradient;
        raw.color = Color.white; raw.raycastTarget = false;
    }
}
