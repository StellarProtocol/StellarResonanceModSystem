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
///   Accent    (left strip — baked LEFT-rounded sprite, kind-tinted, full-height, ignore-layout)
///   Content   (VerticalLayoutGroup + ContentSizeFitter, inset right of accent)
///     Header  (Icon + Title)
///     Message
///   Countdown (Image Filled Horizontal-Left, bottom edge, kind-colour)
/// </code>
/// <para>The accent's rounded left corners come from a <b>baked left-rounded sprite</b>
/// (<see cref="ToastThemeAssets.AccentSprite"/>), NOT a stencil <c>Mask</c> — a uGUI Mask
/// combined with a fading <see cref="CanvasGroup"/> is a known IL2CPP stencil/alpha/batching
/// flicker combo. The sprite renders identically in the Mono sandbox and in-game IL2CPP.</para>
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
    // The accent strip's rounded left corners use the card radius; the strip is drawn wide enough
    // that the rounded arc reads (the visible coloured stripe is AccentWidth; the extra radius px is
    // the rounded-corner zone that mirrors the card silhouette).
    internal const int CardCornerRadius = 8;
    internal const float AccentSpriteWidth = AccentWidth + CardCornerRadius;

    private readonly ToastThemeAssets _assets;

    public ToastCardBuilder(ToastThemeAssets assets) => _assets = assets;

    /// <summary>Build a card for <paramref name="message"/>/<paramref name="kind"/> under
    /// <paramref name="parent"/>. The returned handle exposes the components the animator drives.
    /// The card is measured once (full nested layout pass) then FROZEN — <b>every</b>
    /// <see cref="ContentSizeFitter"/> and layout group in the card sub-hierarchy (card, inner
    /// Content column, Header row, and each text slot) is disabled after the single measure pass,
    /// so the animator can tween localScale/alpha/anchoredPosition on a fully static hierarchy with
    /// no per-frame layout churn anywhere (the size/text-flicker fix).</summary>
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

        // NO stencil mask. The Accent is a baked LEFT-rounded sprite drawn at the card's left edge —
        // its rounded left corners mirror the card silhouette by construction. Bg / Accent / Countdown
        // are ignore-layout card children; only Content drives the measured height.
        BuildBg(card.transform);
        BuildAccent(card.transform, kindColor);
        var content = BuildContent(card.transform);
        BuildHeader(content.transform, message, kind, kindColor);
        var countdown = BuildCountdown(card.transform, kindColor);

        FreezeSize(card, rect, cardLayout, cardFit);

        return new ToastCard { Root = card, Rect = rect, Group = group, Content = content, Countdown = countdown };
    }

    // Measure the multi-line card once (nested rebuild measures the wrapped Message height), capture the
    // resulting width/height as a fixed sizeDelta, then DISABLE every layout-driving component in the whole
    // card sub-hierarchy — not just the card-level fitter/VLG, but the inner Content VerticalLayoutGroup,
    // the Header HorizontalLayoutGroup, and each text-slot layout group. After this the card is a fully
    // static unit: positions and sizes are baked, so the scale/alpha tween cannot trigger any re-layout
    // (the text-flicker fix). The animator tweens scale/alpha/Y only; the renderer reads this frozen height.
    private static void FreezeSize(GameObject card, RectTransform rect, VerticalLayoutGroup cardLayout, ContentSizeFitter cardFit)
    {
        // One settling pass with the card-level fitter still live so the wrapped Message height resolves.
        LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        float w = rect.rect.width;
        float h = rect.rect.height;
        cardFit.enabled = false;
        cardLayout.enabled = false;
        rect.sizeDelta = new Vector2(w, h);

        // Freeze EVERYTHING below the card too, so no descendant can re-lay-out while the card tweens.
        FreezeDescendantLayout(card.transform);
    }

    // Disable every ContentSizeFitter and layout group at/under <paramref name="root"/> so the measured
    // child positions/sizes become static. Runs after the single measure pass; the GetComponentsInChildren
    // walk is one-shot at build time (not on the per-frame path), so its cost is irrelevant to the tween.
    private static void FreezeDescendantLayout(Transform root)
    {
        foreach (var lg in root.GetComponentsInChildren<LayoutGroup>(includeInactive: true))
            lg.enabled = false;
        foreach (var fit in root.GetComponentsInChildren<ContentSizeFitter>(includeInactive: true))
            fit.enabled = false;
    }

    private GameObject BuildBg(Transform parent)
    {
        var bg = UGuiPrimitives.NewChild("Bg", parent);
        bg.AddComponent<LayoutElement>().ignoreLayout = true;
        UGuiPrimitives.Stretch(bg);
        var img = bg.AddComponent<Image>();
        img.sprite = _assets.CardBgSprite; img.type = Image.Type.Sliced; img.raycastTarget = false;
        // No Mask — the accent rounds its own left corners via a baked sprite (see BuildAccent).
        return bg;
    }

    private void BuildAccent(Transform parent, Color kindColor)
    {
        var accent = UGuiPrimitives.NewChild("Accent", parent);
        accent.AddComponent<LayoutElement>().ignoreLayout = true;
        var rt = accent.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(0f, 1f);   // left edge, full height
        rt.pivot = new Vector2(0f, 0.5f);
        // Width = AccentWidth + corner radius so the rounded left corner zone reads. The straight right
        // band of the sliced sprite stretches; the rounded TL/BL corners stay fixed at the card radius.
        rt.offsetMin = Vector2.zero; rt.offsetMax = new Vector2(AccentSpriteWidth, 0f);
        var img = accent.AddComponent<Image>();
        // Baked LEFT-rounded white strip, 9-sliced, tinted per-kind. NO stencil mask anywhere.
        img.sprite = _assets.AccentSprite; img.type = Image.Type.Sliced; img.color = kindColor; img.raycastTarget = false;
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
        // Inset horizontally so the bar's square ends stay INSIDE the card's rounded bottom
        // corners (left clears the accent strip, right clears the corner radius). A full-width
        // bar pokes past the rounded corners and squares off the bottom-left/right.
        rt.offsetMin = new Vector2(AccentSpriteWidth, 0f);
        rt.offsetMax = new Vector2(-CardCornerRadius, CountdownH);
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
