using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Theme;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// uGUI sprite + colour assets for the animated toast renderer. Bakes the dark rounded
/// card background (9-slice), a 1×1 white accent/countdown sprite (tinted per-kind by the
/// builder), and one procedurally-drawn icon glyph per <see cref="NotificationKind"/>.
/// Mirrors <see cref="HudThemeAssets"/> + <see cref="RoundedTextureBaker"/>.
/// </summary>
/// <remarks>
/// <para><b>Card bg is theme-invariant</b> (dark/opaque rgba(20,22,28,0.92)) so toasts stay
/// readable over the near-white Light preset — it does NOT follow <c>MenuBackground</c>.</para>
/// <para><b>Kind colours are fixed semantic constants</b>, not theme tokens: the Crimson preset
/// flips Accent→red / Warning→yellow which would make Success read red and Error read yellow.
/// See <see cref="KindColor"/>.</para>
/// Owns its Texture2D + Sprite lifetime; <see cref="Rebake"/> destroys the prior set first.
/// All assets are HideAndDontSave.
/// </remarks>
internal sealed class ToastThemeAssets
{
    // Dark, opaque card background — NOT themed (Light-preset readability). rgba(20,22,28,0.92).
    internal static readonly ColorRgba CardBg = new(20f / 255f, 22f / 255f, 28f / 255f, 0.92f);

    // FIXED semantic kind colours (theme-invariant). Info / Success / Warning / Error.
    private static readonly ColorRgba InfoColor    = ColorRgba.FromHex(0x5AA0E8ffu);
    private static readonly ColorRgba SuccessColor = ColorRgba.FromHex(0x4CC15Cffu);
    private static readonly ColorRgba WarningColor = ColorRgba.FromHex(0xF4A23Fffu);
    private static readonly ColorRgba ErrorColor   = ColorRgba.FromHex(0xE5484Dffu);

    // Card bg bake: 24px rounded with corner radius 8 (matches CardCornerRadius), 9-slice border 8.
    private const int CardTexSize = 24;
    private const int CardRadius = 8;
    // The accent strip rounds with its OWN smaller radius (not the card's) so the coloured
    // left bar reads thin; ToastCardBuilder sizes the strip to AccentWidth + AccentRadius.
    internal const int AccentRadius = 4;

    private const int IconTexSize = 32;   // baked at 2× the 16px on-screen IconSize for crisp downscale

    private Texture2D? _cardTex;
    private Texture2D? _whiteTex;
    private Texture2D? _accentTex;
    private readonly Sprite?[] _iconSprites = new Sprite?[4];
    private readonly Texture2D?[] _iconTex = new Texture2D?[4];

    /// <summary>Rounded dark card background (9-slice).</summary>
    public Sprite? CardBgSprite { get; private set; }

    /// <summary>Flat 1×1 white sprite — tinted per-kind by the builder for the
    /// countdown fill.</summary>
    public Sprite? WhiteSprite { get; private set; }

    /// <summary>White left-rounded strip sprite (TL+BL corners radius 8, right edge
    /// square), 9-sliced so it stretches to full card height with fixed corners. The
    /// builder tints it per-kind and draws it at the card's left edge — the rounded
    /// left corners follow the card silhouette WITHOUT a stencil mask.</summary>
    public Sprite? AccentSprite { get; private set; }

    /// <summary>Primary HUD text colour (themed — message body reads as HUD text).</summary>
    public Color HudText { get; private set; } = Color.white;

    /// <summary>Drop-shadow under HUD text.</summary>
    public Color HudTextShadow { get; private set; } = new Color(0f, 0f, 0f, 0.85f);

    public bool IsBaked => CardBgSprite != null && WhiteSprite != null && AccentSprite != null;

    /// <summary>The fixed semantic colour for a notification kind (theme-invariant).</summary>
    public static ColorRgba KindColor(NotificationKind kind) => kind switch
    {
        NotificationKind.Success => SuccessColor,
        NotificationKind.Warning => WarningColor,
        NotificationKind.Error   => ErrorColor,
        _                        => InfoColor,
    };

    /// <summary>Per-kind icon glyph sprite (info dot / check / bang / cross), already kind-tinted.</summary>
    public Sprite? IconFor(NotificationKind kind) => _iconSprites[(int)kind];

    /// <summary>Bakes the asset set from <paramref name="colors"/> only if not already baked.</summary>
    public void EnsureBaked(IThemeHudColors colors) { if (!IsBaked) Rebake(colors); }

    /// <summary>Destroys the prior set (if any) and bakes a fresh one. Card bg + kind colours are
    /// theme-invariant; only the HUD text colours come from <paramref name="colors"/>.</summary>
    public void Rebake(IThemeHudColors colors)
    {
        DestroyAll();
        _cardTex = RoundedTextureBaker.Rounded(CardTexSize, CardRadius, CardBg);
        CardBgSprite = MakeSlicedSprite(_cardTex, CardRadius);
        _whiteTex = MakeWhiteTex();
        WhiteSprite = MakeSimpleSprite(_whiteTex);
        // White left-rounded strip (builder tints per-kind). 9-slice border rounds only the
        // left corners (left=radius, right=0, top/bottom=radius) so it stretches to card height.
        _accentTex = RoundedTextureBaker.RoundedLeft(CardTexSize, AccentRadius, new ColorRgba(1f, 1f, 1f, 1f));
        AccentSprite = MakeSlicedSprite(_accentTex, new Vector4(AccentRadius, AccentRadius, 0f, AccentRadius));
        BakeIcons();
        HudText = ToColor(colors.HudText);
        HudTextShadow = ToColor(colors.HudTextShadow);
    }

    public void DestroyAll()
    {
        if (CardBgSprite != null) Object.Destroy(CardBgSprite);
        CardBgSprite = null;
        if (_cardTex != null) Object.Destroy(_cardTex);
        _cardTex = null;

        if (WhiteSprite != null) Object.Destroy(WhiteSprite);
        WhiteSprite = null;
        if (_whiteTex != null) Object.Destroy(_whiteTex);
        _whiteTex = null;

        if (AccentSprite != null) Object.Destroy(AccentSprite);
        AccentSprite = null;
        if (_accentTex != null) Object.Destroy(_accentTex);
        _accentTex = null;

        for (var i = 0; i < _iconSprites.Length; i++)
        {
            if (_iconSprites[i] != null) Object.Destroy(_iconSprites[i]);
            _iconSprites[i] = null;
            if (_iconTex[i] != null) Object.Destroy(_iconTex[i]);
            _iconTex[i] = null;
        }
    }

    private void BakeIcons()
    {
        for (var k = 0; k < 4; k++)
        {
            var kind = (NotificationKind)k;
            var tex = ToastIconBaker.Bake(IconTexSize, kind, KindColor(kind));
            _iconTex[k] = tex;
            _iconSprites[k] = MakeSimpleSprite(tex);
        }
    }

    private static Texture2D MakeWhiteTex()
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        tex.SetPixel(0, 0, Color.white);
        tex.Apply(updateMipmaps: false);
        return tex;
    }

    private static Sprite MakeSimpleSprite(Texture2D tex)
    {
        var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private static Sprite MakeSlicedSprite(Texture2D tex, int border)
        => MakeSlicedSprite(tex, new Vector4(border, border, border, border));

    // border = (left, bottom, right, top) px — Unity's Sprite border convention. Asymmetric
    // borders let the left-rounded accent stretch its straight right band while the rounded
    // left corners stay fixed (right border = 0).
    private static Sprite MakeSlicedSprite(Texture2D tex, Vector4 border)
    {
        var sprite = Sprite.Create(
            tex,
            new Rect(0f, 0f, tex.width, tex.height),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit: 100f,
            extrude: 0,
            meshType: SpriteMeshType.FullRect,
            border: border);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private static Color ToColor(ColorRgba c) => new(c.R, c.G, c.B, c.A);
}
