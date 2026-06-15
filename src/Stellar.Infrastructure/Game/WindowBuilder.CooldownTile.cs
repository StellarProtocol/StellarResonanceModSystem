using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// WindowBuilder support for <see cref="CooldownTileElement"/> — the CooldownBar's icon tile: an accent-outlined
/// icon square (game-asset RawImage + UV) with a foot fill-bar (fractional width via anchorMax.x — sprite-less
/// fills must size by anchors, not Image.fillAmount; see reference_ugui_filled_image_needs_sprite), a ★ corner
/// badge for imagine lockouts, an optional ×N charge badge, and a centred seconds caption below. The art binds on
/// <see cref="WindowToken"/> Apply (not the runtime ticker), so the tile renders in the sandbox as well as in-game.
/// </summary>
internal sealed partial class WindowBuilder
{
    private const float CdTileIcon = 44f;
    private static Color CdCol(ColorRgba c) => new(c.R, c.G, c.B, c.A);
    private static readonly Color CdInsetBg = new(0.10f, 0.12f, 0.16f, 0.95f);   // dark tile body inside the outline
    private static readonly Color CdLoadBg  = new(0.16f, 0.20f, 0.26f, 1f);      // neutral square while art loads / is null
    private static readonly Color CdStarCol = new(1f, 0.81f, 0.30f, 1f);         // imagine ★ gold
    private static readonly Color CdChgCol  = new(1f, 0.86f, 0.40f, 1f);         // charge badge gold

    private void BuildCooldownTile(CooldownTileElement ct, Transform parent, WindowToken token)
    {
        var root = UGuiPrimitives.NewChild("CdTile", parent);
        var vlg = root.AddComponent<VerticalLayoutGroup>();
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = false; vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperCenter; vlg.spacing = 2f;
        var rle = root.AddComponent<LayoutElement>(); rle.minWidth = rle.preferredWidth = CdTileIcon; rle.flexibleWidth = 0f;

        var iconBox = UGuiPrimitives.NewChild("IconBox", root.transform);
        var ile = iconBox.AddComponent<LayoutElement>();
        ile.minWidth = ile.preferredWidth = CdTileIcon; ile.minHeight = ile.preferredHeight = CdTileIcon; ile.flexibleWidth = 0f;

        var outline = BuildCdIconSquare(iconBox.transform, out var art, out var fillRt, out var fillImg);

        var star = AddOverlayText(token, iconBox.transform, "Star", TextAnchor.UpperRight, 11);
        star.color = CdStarCol; star.text = "★"; star.gameObject.SetActive(false);
        var charge = AddOverlayText(token, iconBox.transform, "Chg", TextAnchor.LowerRight, 9);
        charge.color = CdChgCol; charge.gameObject.SetActive(false);

        var secsGo = UGuiPrimitives.NewChild("Secs", root.transform);
        var secs = secsGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(secs, Scaled(12), TextAnchor.MiddleCenter, bold: true);
        ApplyMenuFont(secs); secs.horizontalOverflow = HorizontalWrapMode.Overflow; secs.color = Color.white;
        RegisterTextSizeReskin(token, secs, 12);

        token.CooldownTiles.Add(new CooldownTileBinding
        {
            El = ct, Outline = outline, Art = art, FillRt = fillRt, FillImg = fillImg, Secs = secs,
            StarGo = star.gameObject, ChargeGo = charge.gameObject, Charge = charge,
        });
    }

    // Builds the icon square's layers: accent outline ring (returned), dark inset, art RawImage, foot fill-bar.
    private Image BuildCdIconSquare(Transform iconBox, out RawImage art, out RectTransform fillRt, out Image fillImg)
    {
        var outlineGo = UGuiPrimitives.NewChild("Outline", iconBox);
        UGuiPrimitives.Stretch(outlineGo);
        var outline = outlineGo.AddComponent<Image>();
        outline.sprite = _assets.PanelBg; outline.type = Image.Type.Sliced; outline.raycastTarget = false;

        var insetGo = UGuiPrimitives.NewChild("Inset", iconBox);
        var irt = insetGo.GetComponent<RectTransform>();
        irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
        irt.offsetMin = new Vector2(2f, 2f); irt.offsetMax = new Vector2(-2f, -2f);
        var insetImg = insetGo.AddComponent<Image>();
        insetImg.sprite = _assets.PanelBg; insetImg.type = Image.Type.Sliced; insetImg.color = CdInsetBg; insetImg.raycastTarget = false;

        var artGo = UGuiPrimitives.NewChild("Art", insetGo.transform);
        var artRt = artGo.GetComponent<RectTransform>();
        artRt.anchorMin = Vector2.zero; artRt.anchorMax = Vector2.one;
        artRt.offsetMin = new Vector2(2f, 2f); artRt.offsetMax = new Vector2(-2f, -2f);
        art = artGo.AddComponent<RawImage>(); art.raycastTarget = false; art.enabled = false;

        // Foot fill-bar: width = fraction via anchorMax.x (sprite-less → anchor-sized, not fillAmount).
        var fillGo = UGuiPrimitives.NewChild("Fill", insetGo.transform);
        fillRt = fillGo.GetComponent<RectTransform>();
        fillRt.anchorMin = new Vector2(0f, 0f); fillRt.anchorMax = new Vector2(0f, 0f); fillRt.pivot = new Vector2(0f, 0f);
        fillRt.sizeDelta = new Vector2(0f, 5f); fillRt.anchoredPosition = Vector2.zero;
        fillImg = fillGo.AddComponent<Image>(); fillImg.raycastTarget = false;
        return outline;
    }

    // Poll-diffed per Apply: texture/uv, accent (outline+fill+caption), fill width, seconds, ★, charge badge.
    internal sealed class CooldownTileBinding
    {
        public CooldownTileElement El = null!;
        public Image Outline = null!;
        public RawImage Art = null!;
        public RectTransform FillRt = null!;
        public Image FillImg = null!;
        public Text Secs = null!;
        public GameObject StarGo = null!;
        public GameObject ChargeGo = null!;
        public Text Charge = null!;

        private object? _tex; private UvRect _uv; private float _fill = -1f; private string? _secs;
        private ColorRgba _accent; private bool _initAccent; private bool _star; private int _charge = -1;

        public void Apply()
        {
            var tex = El.Icon();
            var uv = El.Uv();
            if (!ReferenceEquals(tex, _tex) || !uv.Equals(_uv))
            {
                _tex = tex; _uv = uv;
                var t = tex as Texture;
                Art.texture = t; Art.enabled = true;
                Art.color = t == null ? CdLoadBg : Color.white;
                Art.uvRect = new Rect(uv.X, uv.Y, uv.W, uv.H);
            }

            var accent = El.Accent();
            if (!_initAccent || !accent.Equals(_accent))
            {
                _initAccent = true; _accent = accent;
                var c = CdCol(accent);
                Outline.color = c; FillImg.color = c; Secs.color = c;
            }

            var f = El.Fill01(); if (f < 0f) f = 0f; else if (f > 1f) f = 1f;
            if (!Mathf.Approximately(f, _fill)) { _fill = f; var a = FillRt.anchorMax; a.x = f; FillRt.anchorMax = a; }

            var s = El.Seconds(); if (s != _secs) { _secs = s; Secs.text = s; }

            var star = El.IsImagine(); if (star != _star) { _star = star; StarGo.SetActive(star); }

            var ch = El.ChargeCount();
            if (ch != _charge) { _charge = ch; var show = ch > 1; ChargeGo.SetActive(show); if (show) Charge.text = "×" + ch; }
        }
    }
}
