using Stellar.Abstractions.Domain;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// WindowBuilder support for the meter row's trailing Battle-Imagine icons (up to 2 per row, after the share%).
/// Each cell = card art (<see cref="RawImage"/>) + a radial cooldown sweep (a Filled/Radial360 <see cref="Image"/>
/// over a generated white-disc sprite — a sprite-less Filled image would ignore fillAmount, see
/// reference_ugui_filled_image_needs_sprite) + an optional charge badge + optional remaining-seconds text.
/// Built once per row; <see cref="MeterRowBinding"/> poll-diffs the per-frame state via <see cref="BindImagineCell"/>.
/// </summary>
internal sealed partial class WindowBuilder
{
    private const float MeterImagineSize   = 20f;   // icon square edge (px) — Small
    private const float MeterImagineSizeLg = 26f;   // icon square edge (px) — Large (fits the 48px row w/ bottom pad trimmed)

    private static readonly Color MeterCdScrim     = new(0f, 0f, 0f, 0.58f);   // dark overlay on the un-recharged arc
    private static readonly Color MeterImagineDim  = new(0.62f, 0.62f, 0.62f, 1f);   // tint for inferred (other-player) icons
    private static readonly Color MeterImagineLoad = new(0.16f, 0.20f, 0.26f, 1f);   // neutral card backing while art loads / is unavailable

    // Handles for one trailing Imagine cell — owned by the row, mutated by the binding. Layout is a horizontal
    // group: [icon square (art+sweep+charge)] + [meta column: level / cooldown-seconds]. The meta text sits
    // BESIDE the icon (never overlaid) so it can't hide the card art at small size.
    internal sealed class ImagineCell
    {
        public GameObject Root = null!;
        public LayoutElement IconLe = null!;   // icon square sizer (Small/Large)
        public RawImage Art = null!;
        public Image Sweep = null!;
        public Text Charge = null!;
        public GameObject ChargeGo = null!;
        public Text Secs = null!;              // "8s" beside the icon
        public GameObject SecsGo = null!;
    }

    // White filled disc as a Sprite so Image.Type.Filled/Radial360 has geometry to clip (cached, shared).
    private Sprite? _cdSprite;
    private Sprite CooldownSprite()
    {
        if (_cdSprite != null) return _cdSprite;
        const int d = 64; var r = d * 0.5f;
        var t = new Texture2D(d, d, TextureFormat.RGBA32, false)
        { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (var y = 0; y < d; y++)
        for (var x = 0; x < d; x++)
        {
            float dist = Mathf.Sqrt((x - r + 0.5f) * (x - r + 0.5f) + (y - r + 0.5f) * (y - r + 0.5f));
            float a = Mathf.Clamp01(r - dist);   // 1px feathered edge
            t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        t.Apply();
        _cdSprite = Sprite.Create(t, new Rect(0, 0, d, d), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        return _cdSprite;
    }

    // Build one trailing icon cell: card art + radial cooldown sweep + a centered cooldown-seconds overlay
    // + a charge badge. The icon square resizes via IconLe (Small/Large). All initially hidden.
    private ImagineCell BuildImagineCell(WindowToken token, Transform line)
    {
        var root = UGuiPrimitives.NewChild("Imagine", line);
        var iconLe = root.AddComponent<LayoutElement>(); iconLe.preferredWidth = iconLe.preferredHeight = MeterImagineSize;

        var artGo = UGuiPrimitives.NewChild("Art", root.transform);
        UGuiPrimitives.Stretch(artGo);
        var art = artGo.AddComponent<RawImage>(); art.raycastTarget = false; art.color = Color.white;

        var sweepGo = UGuiPrimitives.NewChild("Sweep", root.transform);
        UGuiPrimitives.Stretch(sweepGo);
        var sweep = sweepGo.AddComponent<Image>();
        sweep.sprite = CooldownSprite(); sweep.color = MeterCdScrim; sweep.raycastTarget = false;
        sweep.type = Image.Type.Filled; sweep.fillMethod = Image.FillMethod.Radial360;
        sweep.fillOrigin = 2 /* Origin360.Top */; sweep.fillClockwise = true; sweep.fillAmount = 0f;

        // Cooldown seconds — centered over the icon (old design), drawn atop the grey radial sweep.
        var secs = AddOverlayText(token, root.transform, "Secs", TextAnchor.MiddleCenter, baseSize: 9);

        // Charge badge: dark pill + gold count, bottom-right of the icon (bg keeps the count legible over art).
        var chargeGo = UGuiPrimitives.NewChild("Chg", root.transform);
        var crt = chargeGo.GetComponent<RectTransform>();
        crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1f, 0f);   // bottom-right
        crt.sizeDelta = new Vector2(13f, 12f); crt.anchoredPosition = new Vector2(2f, -2f);
        var chgBg = chargeGo.AddComponent<Image>(); chgBg.color = new Color(0.04f, 0.05f, 0.07f, 0.92f); chgBg.raycastTarget = false;
        var chgTextGo = UGuiPrimitives.NewChild("Txt", chargeGo.transform);
        UGuiPrimitives.Stretch(chgTextGo);
        var charge = chgTextGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(charge, Scaled(9), TextAnchor.MiddleCenter, bold: true);
        ApplyMenuFont(charge); charge.color = new Color(1f, 0.86f, 0.4f, 1f);
        RegisterTextSizeReskin(token, charge, 9);

        root.SetActive(false); chargeGo.SetActive(false); secs.gameObject.SetActive(false);
        return new ImagineCell
        {
            Root = root, IconLe = iconLe, Art = art, Sweep = sweep, Charge = charge, ChargeGo = chargeGo,
            Secs = secs, SecsGo = secs.gameObject,
        };
    }

    // Poll-diff one cell from its slot. Called from MeterRowBinding.Apply for Imagine0/Imagine1. Cooldown
    // seconds render centered over the icon (old design); size sets the icon px.
    private static void BindImagineCell(ImagineCell cell, in ImagineSlot slot, in ImagineOpts opts, ref ImagineCellCache cache)
    {
        if (cell.Root == null) return;
        bool showSecs = opts.ShowSecs; var size = opts.Size;
        bool has = opts.ShowImagine && slot.HasImagine;   // ShowImagine=false hides the cell entirely
        if (cache.Init && has == cache.Has && ReferenceEquals(slot.IconTexture, cache.Tex)
            && slot.IconUv.Equals(cache.Uv)
            && Mathf.Approximately(slot.CooldownFraction, cache.Cd) && slot.ChargesAvailable == cache.Charges
            && slot.ChargeCount == cache.ChargeCount && slot.RemainingSeconds == cache.Secs
            && slot.Inferred == cache.Inferred
            && showSecs == cache.ShowSecs && size == cache.Size) return;

        if (has != cache.Has || !cache.Init) cell.Root.SetActive(has);
        if (has)
        {
            float px = size == ImagineSize.Large ? MeterImagineSizeLg : MeterImagineSize;
            cell.IconLe.preferredWidth = cell.IconLe.preferredHeight = px;
            cell.Art.texture = slot.IconTexture as Texture;
            cell.Art.enabled = true;   // always a card backing — neutral square while the art loads / is unavailable
            cell.Art.uvRect = new Rect(slot.IconUv.X, slot.IconUv.Y, slot.IconUv.W, slot.IconUv.H);
            cell.Art.color = slot.IconTexture == null ? MeterImagineLoad
                           : slot.Inferred ? MeterImagineDim : Color.white;
            cell.Sweep.fillAmount = Mathf.Clamp01(slot.CooldownFraction);

            bool showChg = slot.ChargeCount > 1;
            cell.ChargeGo.SetActive(showChg);
            if (showChg) cell.Charge.text = slot.ChargesAvailable.ToString();

            bool showSecsNow = showSecs && slot.RemainingSeconds > 0;
            cell.SecsGo.SetActive(showSecsNow);
            if (showSecsNow) cell.Secs.text = slot.RemainingSeconds.ToString();
        }

        cache = new ImagineCellCache
        {
            Init = true, Has = has, Tex = slot.IconTexture, Uv = slot.IconUv, Cd = slot.CooldownFraction,
            Charges = slot.ChargesAvailable, ChargeCount = slot.ChargeCount, Secs = slot.RemainingSeconds,
            Inferred = slot.Inferred, ShowSecs = showSecs, Size = size,
        };
    }

    // Per-row imagine display options (bundled to keep BindImagineCell within the parameter cap).
    internal readonly record struct ImagineOpts(bool ShowImagine, bool ShowSecs, ImagineSize Size);

    // Per-cell poll-diff cache (mirrors the other MeterRowBinding scalar caches).
    internal struct ImagineCellCache
    {
        public bool Init, Has, Inferred, ShowSecs;
        public object? Tex;
        public UvRect Uv;
        public float Cd;
        public int Charges, ChargeCount, Secs;
        public ImagineSize Size;
    }
}
