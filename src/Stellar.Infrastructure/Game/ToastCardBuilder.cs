using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Builds one toast card's uGUI hierarchy from <see cref="ToastThemeAssets"/>. No IL2CPP /
/// game deps — the UI sandbox drives this exact path so the static layout/colour is measured
/// against the spec, not a story-only copy. The card height is content-driven via a
/// <see cref="ContentSizeFitter"/> on the content column; the renderer reads the preferred
/// height to compute stack-Y.
/// </summary>
/// <remarks>
/// Structure (per design spec):
/// <code>
/// Card[CanvasGroup, pivot(0.5,1), w=CardWidth]
///   Bg        (sliced rounded dark, stretched, ignore-layout)
///   Accent    (left strip, kind-colour, full-height, ignore-layout)
///   Content   (VerticalLayoutGroup + ContentSizeFitter, inset right of accent)
///     Header  (Icon + Title)
///     Message
///   Countdown (Image Filled Horizontal-Left, bottom edge, kind-colour)
/// </code>
/// Shadowed text uses the manual sibling-twin trick (<c>UnityEngine.UI.Shadow</c> is interop-stripped).
/// </remarks>
internal sealed class ToastCardBuilder
{
    // Card geometry tokens (px) — verbatim from the design spec.
    internal const float CardWidth = 340f;
    internal const int PadT = 10, PadR = 12, PadB = 10, PadL = 12;
    internal const float AccentWidth = 4f;
    internal const int IconSize = 16;
    internal const int IconTitleGap = 6;
    internal const int TitleSize = 12;
    internal const int MsgSize = 15;
    internal const int TitleGap = 2;
    internal const float CountdownH = 3f;

    private readonly ToastThemeAssets _assets;

    public ToastCardBuilder(ToastThemeAssets assets) => _assets = assets;

    /// <summary>Build a card for <paramref name="message"/>/<paramref name="kind"/> under
    /// <paramref name="parent"/>. The returned handle exposes the components the animator drives.
    /// The card is measured once (full nested layout pass) then FROZEN to a fixed size — the
    /// <see cref="ContentSizeFitter"/> and the size-controlling <see cref="VerticalLayoutGroup"/> are
    /// disabled so the animator can tween localScale/alpha/anchoredPosition on a static rect with no
    /// per-frame layout churn (the size-flicker fix).</summary>
    public ToastCard Build(Transform parent, string message, NotificationKind kind)
    {
        var kindColor = ToColor(ToastThemeAssets.KindColor(kind));

        var card = UGuiPrimitives.NewChild("Toast", parent);
        var rect = card.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1f);   // top-centre
        rect.sizeDelta = new Vector2(CardWidth, 0f);
        var group = card.AddComponent<CanvasGroup>();

        // Card lays out its single content child and grows its OWN height to fit (the bg / accent /
        // countdown are ignore-layout children anchored to the card, so they don't affect the measure).
        // This drives the initial measure; we read the result and then freeze the size (see below).
        var cardLayout = card.AddComponent<VerticalLayoutGroup>();
        cardLayout.childControlWidth = true; cardLayout.childControlHeight = true;
        cardLayout.childForceExpandWidth = true; cardLayout.childForceExpandHeight = false;
        var cardFit = card.AddComponent<ContentSizeFitter>();
        cardFit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Bg owns a rounded Mask; Accent is a CHILD of Bg so its square left corners are clipped to the
        // card's rounded silhouette (the square-left-corner fix). Content/Countdown stay card children.
        var bg = BuildBg(card.transform);
        BuildAccent(bg.transform, kindColor);
        var content = BuildContent(card.transform);
        BuildHeader(content.transform, message, kind, kindColor);
        var countdown = BuildCountdown(card.transform, kindColor);

        FreezeSize(card, rect, cardLayout, cardFit);

        return new ToastCard { Root = card, Rect = rect, Group = group, Content = content, Countdown = countdown };
    }

    // Measure the multi-line card once (nested rebuild measures the wrapped Message height), capture the
    // resulting width/height as a fixed sizeDelta, then DISABLE the fitter + size-controlling layout group
    // so subsequent frames don't recompute layout. After this the card is a static-size unit: the animator
    // tweens scale/alpha/Y only, and the renderer reads this frozen height for stack-Y.
    private static void FreezeSize(GameObject card, RectTransform rect, VerticalLayoutGroup cardLayout, ContentSizeFitter cardFit)
    {
        LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        float w = rect.rect.width;
        float h = rect.rect.height;
        cardFit.enabled = false;
        cardLayout.enabled = false;
        rect.sizeDelta = new Vector2(w, h);
    }

    private GameObject BuildBg(Transform parent)
    {
        var bg = UGuiPrimitives.NewChild("Bg", parent);
        bg.AddComponent<LayoutElement>().ignoreLayout = true;
        UGuiPrimitives.Stretch(bg);
        var img = bg.AddComponent<Image>();
        img.sprite = _assets.CardBgSprite; img.type = Image.Type.Sliced; img.raycastTarget = false;
        // Rounded stencil mask: children (the Accent) are clipped to this sprite's alpha → the accent's
        // left corners follow the card's 8px radius. showMaskGraphic keeps the bg itself visible.
        var mask = bg.AddComponent<Mask>();
        mask.showMaskGraphic = true;
        return bg;
    }

    private void BuildAccent(Transform parent, Color kindColor)
    {
        var accent = UGuiPrimitives.NewChild("Accent", parent);
        accent.AddComponent<LayoutElement>().ignoreLayout = true;
        var rt = accent.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(0f, 1f);   // left edge, full height
        rt.pivot = new Vector2(0f, 0.5f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = new Vector2(AccentWidth, 0f);
        var img = accent.AddComponent<Image>();
        img.sprite = _assets.WhiteSprite; img.type = Image.Type.Simple; img.color = kindColor; img.raycastTarget = false;
        img.maskable = true;   // clipped to Bg's rounded mask → rounded left corners
    }

    private GameObject BuildContent(Transform parent)
    {
        // Laid out by the card's VerticalLayoutGroup (full card width). Its own VLayout + padding
        // arranges Header + Message and reports the preferred height that drives the card height.
        // Left padding includes the accent strip width so text clears the kind-colour accent.
        var content = UGuiPrimitives.NewChild("Content", parent);
        var v = content.AddComponent<VerticalLayoutGroup>();
        v.padding = new RectOffset(PadL + (int)AccentWidth, PadR, PadT, PadB);
        v.spacing = TitleGap;
        v.childControlWidth = true; v.childControlHeight = true;
        v.childForceExpandWidth = true; v.childForceExpandHeight = false;
        v.childAlignment = TextAnchor.UpperLeft;
        return content;
    }

    private void BuildHeader(Transform parent, string message, NotificationKind kind, Color kindColor)
    {
        var header = UGuiPrimitives.NewChild("Header", parent);
        var h = header.AddComponent<HorizontalLayoutGroup>();
        h.spacing = IconTitleGap;
        h.childControlWidth = true; h.childControlHeight = true;
        h.childForceExpandWidth = false; h.childForceExpandHeight = false;
        h.childAlignment = TextAnchor.MiddleLeft;

        var icon = UGuiPrimitives.NewChild("Icon", header.transform);
        UGuiPrimitives.SetPreferred(icon, IconSize, IconSize);
        var iimg = icon.AddComponent<Image>();
        iimg.sprite = _assets.IconFor(kind); iimg.type = Image.Type.Simple; iimg.preserveAspect = true; iimg.raycastTarget = false;

        var (_, tfg, tsh) = MakeShadowedText(header.transform, TitleSize, TextAnchor.MiddleLeft, bold: true);
        var title = TitleFor(kind);
        tfg.text = title; tsh.text = title; tfg.color = kindColor;

        var (mslot, mfg, msh) = MakeShadowedText(parent, MsgSize, TextAnchor.UpperLeft, bold: false);
        mfg.horizontalOverflow = HorizontalWrapMode.Wrap; msh.horizontalOverflow = HorizontalWrapMode.Wrap;
        mfg.text = message; msh.text = message;
        var le = mslot.AddComponent<LayoutElement>();
        le.flexibleWidth = 1f;   // message stretches to the content width so wrap measures correctly
    }

    private Image BuildCountdown(Transform parent, Color kindColor)
    {
        var cd = UGuiPrimitives.NewChild("Countdown", parent);
        cd.AddComponent<LayoutElement>().ignoreLayout = true;
        var rt = cd.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f); rt.pivot = new Vector2(0f, 0f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = new Vector2(0f, CountdownH);
        var img = cd.AddComponent<Image>();
        img.sprite = _assets.WhiteSprite; img.color = kindColor; img.raycastTarget = false;
        img.type = Image.Type.Filled; img.fillMethod = Image.FillMethod.Horizontal; img.fillOrigin = 0; img.fillAmount = 1f;
        return img;
    }

    private static string TitleFor(NotificationKind kind) => kind switch
    {
        NotificationKind.Success => "SUCCESS",
        NotificationKind.Warning => "WARNING",
        NotificationKind.Error   => "ERROR",
        _                        => "INFO",
    };

    // Sibling-twin shadowed text (UnityEngine.UI.Shadow is interop-stripped). Mirror
    // HudElementBuilder.MakeShadowedText: shadow drawn behind (sibling 0), fg on top sizes the slot.
    private (GameObject Slot, Text Fg, Text Shadow) MakeShadowedText(Transform parent, int fontSize, TextAnchor anchor, bool bold)
    {
        var slot = UGuiPrimitives.NewChild("Text", parent);
        var lg = slot.AddComponent<HorizontalLayoutGroup>();
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = false; lg.childForceExpandHeight = false;
        lg.childAlignment = anchor;

        var shGo = UGuiPrimitives.NewChild("Shadow", slot.transform);
        shGo.AddComponent<LayoutElement>().ignoreLayout = true;
        var shrt = shGo.GetComponent<RectTransform>();
        shrt.anchorMin = Vector2.zero; shrt.anchorMax = Vector2.one;
        shrt.offsetMin = new Vector2(1f, -1f); shrt.offsetMax = new Vector2(1f, -1f);
        var shadow = shGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(shadow, fontSize, anchor, bold);
        shadow.color = _assets.HudTextShadow;

        var fgGo = UGuiPrimitives.NewChild("Fg", slot.transform);
        var fg = fgGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(fg, fontSize, anchor, bold);
        fg.color = _assets.HudText;
        return (slot, fg, shadow);
    }

    private static Color ToColor(ColorRgba c) => new(c.R, c.G, c.B, c.A);
}

/// <summary>Handle to a built toast card — the components the animator/renderer drive.</summary>
internal sealed class ToastCard
{
    public GameObject Root = null!;
    public RectTransform Rect = null!;
    public CanvasGroup Group = null!;
    public GameObject Content = null!;   // the content column whose preferred height = card height
    public Image Countdown = null!;
}
