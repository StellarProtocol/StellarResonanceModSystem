using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Theme;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// uGUI sprite + colour assets for the native HUD renderer, baked from the
/// active theme's HUD palette so the uGUI HUDs reproduce the IMGUI HudOverlay
/// chrome (transparent container, rounded 9-slice pill chip with accent border,
/// rounded 9-slice bar track, shadowed HudText). The same rounded-corner formula
/// as the IMGUI path is used via <see cref="RoundedTextureBaker"/>.
/// </summary>
/// <remarks>
/// Sprites are baked with a 9-slice <c>border</c> matching the IMGUI
/// <c>GUIStyle.border</c> so the rounded corners survive horizontal stretch when
/// the chip/track widens (<see cref="UnityEngine.UI.Image.Type.Sliced"/>).
/// Texture pixels-per-unit = the canvas default (100) so border texels render
/// 1:1 on a ScreenSpaceOverlay canvas — identical on-screen radius to IMGUI.
/// Owns its Texture2D + Sprite lifetime; <see cref="Rebake"/> destroys the prior
/// set first, so theme switches don't leak. All assets are HideAndDontSave.
/// </remarks>
internal sealed class HudThemeAssets
{
    // Pill chip: mirror the IMGUI _hudLevelPillBgTex bake exactly —
    // MakeRoundedBorderedTexture(40, 9, 2, HudPillBg, HudAccent) with a 9-slice
    // border of 11 (radius 9 + 2 so the slice fully contains the corner arc).
    private const int PillTexSize = 40;
    private const int PillRadius  = 9;
    private const int PillBorderPx = 2;
    private const int PillSliceBorder = 11;

    // Bar track: mirror IMGUI _hudBarBgTex — MakeRoundedTexture(16, 3, HudBarBg)
    // with a 9-slice border of 3 matching the corner radius.
    private const int BarTexSize = 16;
    private const int BarRadius  = 3;

    private Texture2D? _pillTex;
    private Texture2D? _barTex;

    public Sprite? PillBg { get; private set; }
    public Sprite? BarBg { get; private set; }
    public Color HudText { get; private set; } = Color.white;
    public Color HudTextShadow { get; private set; } = new Color(0f, 0f, 0f, 0.85f);

    public bool IsBaked => PillBg != null && BarBg != null;

    /// <summary>Bakes the sprite set from <paramref name="colors"/> only if not already baked.</summary>
    public void EnsureBaked(IThemeHudColors colors) { if (!IsBaked) Rebake(colors); }

    /// <summary>Destroys the prior sprite set (if any) and bakes a fresh one from the active palette.</summary>
    public void Rebake(IThemeHudColors colors)
    {
        DestroyAll();
        _pillTex = RoundedTextureBaker.RoundedBordered(PillTexSize, PillRadius, PillBorderPx, colors.HudPillBg, colors.HudAccent);
        PillBg = MakeSlicedSprite(_pillTex, PillSliceBorder);
        _barTex = RoundedTextureBaker.Rounded(BarTexSize, BarRadius, colors.HudBarBg);
        BarBg = MakeSlicedSprite(_barTex, BarRadius);
        HudText = ToColor(colors.HudText);
        HudTextShadow = ToColor(colors.HudTextShadow);
    }

    public void DestroyAll()
    {
        DestroySprite(ref _pillTex, () => PillBg, s => PillBg = s);
        DestroySprite(ref _barTex, () => BarBg, s => BarBg = s);
    }

    private static void DestroySprite(ref Texture2D? tex, System.Func<Sprite?> get, System.Action<Sprite?> set)
    {
        var sprite = get();
        if (sprite != null) Object.Destroy(sprite);
        set(null);
        if (tex != null) Object.Destroy(tex);
        tex = null;
    }

    private static Sprite MakeSlicedSprite(Texture2D tex, int border)
    {
        var sprite = Sprite.Create(
            tex,
            new Rect(0f, 0f, tex.width, tex.height),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit: 100f,
            extrude: 0,
            meshType: SpriteMeshType.FullRect,
            border: new Vector4(border, border, border, border));
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private static Color ToColor(Stellar.Abstractions.Domain.ColorRgba c) => new(c.R, c.G, c.B, c.A);
}
