using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Theme;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// uGUI sprite + colour assets for the interactive window renderer, baked from the active theme's
/// menu palette (<see cref="IThemeMenuColors"/>). Produces the GlassMenu frosted-glass frame
/// (translucent accent-tinted body + accent border, 14-radius 9-slice) and the Outline button chip
/// (6-radius). Sandbox-pure (UnityEngine + the shared <see cref="RoundedTextureBaker"/> only), so it
/// renders headlessly. Mirrors <see cref="HudThemeAssets"/>.
/// </summary>
/// <remarks>
/// SP1 Plan 1: this is the baked-frost approximation of the approved glass redesign (translucent fill +
/// accent border + edge tint). Plan 2 refines it into the full per-preset frosted bake + the other four
/// chrome styles. Live backdrop blur (GrabPass shader) is out of scope (baked appearance only).
/// </remarks>
internal sealed class WindowThemeAssets
{
    // Frame: 14-radius rounded-bordered sprite. Slice border = radius + 2 so the corner arc is fully
    // inside the 9-slice and survives stretch. Texture size comfortably exceeds 2*sliceBorder.
    private const int FrameTexSize = 48;
    private const int FrameRadius = 14;
    private const int FrameBorderPx = 1;
    private const int FrameSlice = 16;

    // Outline button: 6-radius rounded-bordered chip.
    private const int BtnTexSize = 24;
    private const int BtnRadius = 6;
    private const int BtnBorderPx = 1;
    private const int BtnSlice = 8;

    // Toggle capsule: fully-rounded 15-tall pill (radius 7).
    private const int CapsuleTexSize = 16;
    private const int CapsuleRadius = 7;

    // Swatch: 3-radius rounded square (theme-editor colour chips). Slice border = radius + 1.
    private const int SwatchTexSize = 16;
    private const int SwatchRadius = 3;

    private Texture2D? _frameTex, _btnTex, _btnAccentTex, _capsuleTex, _panelTex, _hgradTex, _titleTex, _swatchTex, _btnGlassTex;

    // Live chrome-style providers (set by the renderer → IChromeStyle). The window button picks its sprite
    // from the global Button style when the element doesn't pin one; re-evaluated on a theme change (re-skin).
    public System.Func<MenuButtonStyle>? ButtonStyleProvider;
    public System.Func<MenuScrollbarStyle>? ScrollbarStyleProvider;
    public MenuButtonStyle ButtonStyle => ButtonStyleProvider?.Invoke() ?? MenuButtonStyle.Outline;

    public Sprite? TitleBg { get; private set; }        // title-bar tint: TOP corners rounded at the frame radius, bottom square
    public Sprite? FrameBg { get; private set; }
    public Sprite? ButtonBg { get; private set; }       // Outline: faint fill + accent border
    public Sprite? ButtonAccentBg { get; private set; } // active/accent button (also Filled)
    public Sprite? ButtonGlassBg { get; private set; }  // Glass: translucent fill + soft accent border
    public Sprite? Capsule { get; private set; }        // toggle track + knob (tinted per state); also the mint dot
    public Sprite? PanelBg { get; private set; }        // translucent dark rounded body (Tracker/Party overlay chromes)
    public Sprite? SwatchBg { get; private set; }       // 3-radius rounded square — theme-editor colour chip (tinted at use-site)
    public Texture2D? HGradient { get; private set; }   // accent→transparent horizontal (Tracker/Party divider RawImage)

    public Color MenuText { get; private set; } = Color.white;
    public Color MenuMuted { get; private set; } = new(0.6f, 0.6f, 0.6f, 1f);
    public Color MenuAccent { get; private set; } = Color.cyan;
    public Color MenuBorder { get; private set; } = new(1f, 1f, 1f, 0.12f);
    public Color MenuBackground { get; private set; } = new(0.05f, 0.07f, 0.09f, 1f);

    // Window text font. The builtin "Arial.ttf" resolves in the Unity *editor* (sandbox) but is ABSENT
    // from IL2CPP *player* builds — there the Text falls back to a different font whose wider metrics make
    // each Text's preferred width exceed the constrained Row/scroll-viewport → overflow → clip at the
    // RectMask2D edge (the long-standing in-world clip bug; the sandbox never reproduced it because the
    // editor has Arial). Resolving an OS dynamic font the same way the IMGUI ThemeRenderer does makes the
    // metrics consistent across editor and player. Process-scoped + load-once: Font.CreateDynamicFontFromOSFont
    // returns a shared OS-typeface binding; recreating it would orphan the instance every live Text still
    // references (ThemeRenderer.Fonts hit exactly this — text vanished on reload), so we cache and never
    // destroy it.
    private static Font? _menuFont;
    private static bool _menuFontTried;
    public Font? MenuFont => _menuFont;

    // Same fallback chain as ThemeRenderer.FontFamilyFallbacks (Noto first for the design target; the
    // DejaVu/Liberation tail covers the Proton box; Arial is Unity's always-synthesised last resort).
    private static readonly string[] FontFamilies =
        { "Noto Sans", "NotoSans", "Noto Sans CJK SC", "DejaVu Sans", "Liberation Sans", "Arial" };

    private static void EnsureFont()
    {
        if (_menuFontTried) return;
        _menuFontTried = true;
        try
        {
            _menuFont = Font.CreateDynamicFontFromOSFont(FontFamilies, 14);
            if (_menuFont == null || !_menuFont) _menuFont = Font.CreateDynamicFontFromOSFont("Arial", 14);
        }
        catch { _menuFont = null; }   // null → window Text keeps ConfigureText's builtin attempt (sandbox is fine)
    }

    public bool IsBaked => FrameBg != null && ButtonBg != null && Capsule != null;

    // Live window-body opacity provider (set by the renderer → IChromeStyle.WindowOpacity). The frame body is
    // baked OPAQUE and the opacity is applied as the frame Image's color alpha, polled live by the builder's
    // FrameOpacityBinding — so dragging the opacity slider updates in real time WITHOUT rebaking the sprite or
    // rebuilding the canvas (which flickered). Null in the sandbox → full opacity.
    public System.Func<float>? OpacityProvider;

    // Live font-scale provider (set by the renderer → IChromeStyle.FontScale). The window builder multiplies
    // each Text's base size by this so the Font Scale slider actually affects uGUI windows. Null → 1.0.
    public System.Func<float>? FontScaleProvider;
    public float FontScale => FontScaleProvider?.Invoke() ?? 1f;

    public void EnsureBaked(IThemeMenuColors c) { if (!IsBaked) Rebake(c); }

    public void Rebake(IThemeMenuColors c)
    {
        // Only the THEME-COLOURED sprites are destroyed+rebaked. Capsule + SwatchBg are theme-INDEPENDENT
        // (white, tinted per use) — destroying them here would strand the slider/toggle/swatch/bar/input Images
        // (which keep a direct sprite reference) on a destroyed sprite after a theme change → they vanish until
        // a SetActive cycle (the "sliders gone after a Font Scale slide; a tab switch brings them back" bug).
        // So bake those ONCE (below) and never destroy them on a re-bake.
        DestroyThemed();
        EnsureFont();
        var a = c.MenuAccent;
        // Frosted body baked OPAQUE (alpha 1); the live window opacity is applied at the Image level (see
        // OpacityProvider) so it's real-time + flicker-free.
        var frosted = Translucent(LerpToward(c.MenuBackground, a, 0.12f), 1f);
        var frameBorder = new ColorRgba(a.R, a.G, a.B, 0.55f);
        _frameTex = RoundedTextureBaker.RoundedBordered(FrameTexSize, FrameRadius, FrameBorderPx, frosted, frameBorder);
        FrameBg = Sliced(_frameTex, FrameSlice);

        // Outline button: near-transparent white fill + accent border (matches the IMGUI Outline style).
        _btnTex = RoundedTextureBaker.RoundedBordered(BtnTexSize, BtnRadius, BtnBorderPx,
            new ColorRgba(1f, 1f, 1f, 0.05f), new ColorRgba(a.R, a.G, a.B, 0.70f));
        ButtonBg = Sliced(_btnTex, BtnSlice);
        _btnAccentTex = RoundedTextureBaker.RoundedBordered(BtnTexSize, BtnRadius, BtnBorderPx,
            new ColorRgba(a.R, a.G, a.B, 0.22f), a);
        ButtonAccentBg = Sliced(_btnAccentTex, BtnSlice);
        // Glass: a more-visible translucent white fill + a soft accent border (sits between Outline and Filled).
        _btnGlassTex = RoundedTextureBaker.RoundedBordered(BtnTexSize, BtnRadius, BtnBorderPx,
            new ColorRgba(1f, 1f, 1f, 0.12f), new ColorRgba(a.R, a.G, a.B, 0.45f));
        ButtonGlassBg = Sliced(_btnGlassTex, BtnSlice);

        EnsureNeutralSprites();

        // Overlay-chrome (Tracker/Party) body: translucent dark rounded panel, no border.
        var dark = new ColorRgba(c.MenuBackground.R, c.MenuBackground.G, c.MenuBackground.B, 0.87f);
        _panelTex = RoundedTextureBaker.Rounded(16, 8, dark);
        PanelBg = Sliced(_panelTex, 8);
        _hgradTex = BakeHGradient(a);

        // Title-bar tint sprite: top corners rounded at the FRAME radius (14) so the tint stays exactly
        // inside the frame's rounded top corners (no square-corner leak); bottom stays square.
        _titleTex = BakeTopRounded(FrameTexSize, FrameRadius);
        TitleBg = SlicedV(_titleTex, FrameSlice, 2f, FrameSlice, FrameSlice);

        MenuText = ToColor(c.MenuText);
        MenuMuted = ToColor(c.MenuMuted);
        MenuAccent = ToColor(c.MenuAccent);
        MenuBorder = ToColor(c.MenuBorder);
        MenuBackground = ToColor(c.MenuBackground);
    }

    // Theme-INDEPENDENT neutral sprites (white, tinted per use) — baked ONCE and kept across re-bakes, so the
    // slider/toggle/swatch/bar/input Images that hold their reference don't get stranded on a destroyed sprite.
    private void EnsureNeutralSprites()
    {
        if (Capsule == null)
        {
            _capsuleTex = RoundedTextureBaker.Rounded(CapsuleTexSize, CapsuleRadius, new ColorRgba(1f, 1f, 1f, 1f));
            Capsule = Sliced(_capsuleTex, CapsuleRadius);
        }
        if (SwatchBg == null)   // 15×15 / 3-radius / 1-px-border editor swatch chip
        {
            _swatchTex = RoundedTextureBaker.Rounded(SwatchTexSize, SwatchRadius, new ColorRgba(1f, 1f, 1f, 1f));
            SwatchBg = Sliced(_swatchTex, SwatchRadius + 1);
        }
    }

    // Destroy ONLY the theme-coloured sprites (re-baked on every theme change). Capsule + SwatchBg are kept
    // (theme-independent; destroying them would strand the widgets that hold their reference — see Rebake).
    private void DestroyThemed()
    {
        Drop(ref _frameTex, () => FrameBg, s => FrameBg = s);
        Drop(ref _btnTex, () => ButtonBg, s => ButtonBg = s);
        Drop(ref _btnAccentTex, () => ButtonAccentBg, s => ButtonAccentBg = s);
        Drop(ref _btnGlassTex, () => ButtonGlassBg, s => ButtonGlassBg = s);
        Drop(ref _panelTex, () => PanelBg, s => PanelBg = s);
        Drop(ref _titleTex, () => TitleBg, s => TitleBg = s);
        if (_hgradTex != null) Object.Destroy(_hgradTex);
        _hgradTex = null; HGradient = null;
    }

    public void DestroyAll()
    {
        DestroyThemed();
        Drop(ref _capsuleTex, () => Capsule, s => Capsule = s);
        Drop(ref _swatchTex, () => SwatchBg, s => SwatchBg = s);
    }

    // White sprite with the TOP two corners rounded (radius r), bottom square. 4× supersampled AA.
    // Unity Texture2D y=0 is the bottom row, so "top" corners are the high-y rows.
    private Texture2D BakeTopRounded(int size, int r)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
        { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        const int SS = 4;
        var px = new Color[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var cov = 0f;
                for (var sy = 0; sy < SS; sy++)
                    for (var sx = 0; sx < SS; sx++)
                    {
                        float fx = x + (sx + 0.5f) / SS, fy = y + (sy + 0.5f) / SS;
                        var inside = true;
                        if (fy > size - r && fx < r) { float dx = r - fx, dy = fy - (size - r); if (dx * dx + dy * dy > r * r) inside = false; }
                        else if (fy > size - r && fx > size - r) { float dx = fx - (size - r), dy = fy - (size - r); if (dx * dx + dy * dy > r * r) inside = false; }
                        if (inside) cov += 1f;
                    }
                px[y * size + x] = new Color(1f, 1f, 1f, cov / (SS * SS));
            }
        tex.SetPixels(px); tex.Apply(false);
        return tex;
    }

    // 9-slice sprite with per-edge border (left, bottom, right, top) — for the top-rounded title sprite.
    private static Sprite SlicedV(Texture2D tex, float l, float b, float r, float t)
    {
        var s = Sprite.Create(tex, new UnityEngine.Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f),
            pixelsPerUnit: 100f, extrude: 0, meshType: SpriteMeshType.FullRect, border: new Vector4(l, b, r, t));
        s.hideFlags = HideFlags.HideAndDontSave;
        return s;
    }

    private const int HGradN = 64;
    private Texture2D BakeHGradient(ColorRgba accent)
    {
        var tex = new Texture2D(HGradN, 1, TextureFormat.RGBA32, mipChain: false)
        { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color[HGradN];
        for (var x = 0; x < HGradN; x++)
        {
            var t = (float)x / (HGradN - 1);
            px[x] = new Color(accent.R, accent.G, accent.B, Mathf.Lerp(0.7f, 0f, t));   // solid mint → transparent
        }
        tex.SetPixels(px); tex.Apply();
        HGradient = tex;
        return tex;
    }

    private static void Drop(ref Texture2D? tex, System.Func<Sprite?> get, System.Action<Sprite?> set)
    {
        var s = get(); if (s != null) Object.Destroy(s); set(null);
        if (tex != null) Object.Destroy(tex); tex = null;
    }

    private static Sprite Sliced(Texture2D tex, int border)
    {
        var sprite = Sprite.Create(tex, new UnityEngine.Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f),
            pixelsPerUnit: 100f, extrude: 0, meshType: SpriteMeshType.FullRect,
            border: new Vector4(border, border, border, border));
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private static ColorRgba LerpToward(ColorRgba from, ColorRgba to, float t) => new(
        from.R + (to.R - from.R) * t, from.G + (to.G - from.G) * t, from.B + (to.B - from.B) * t, from.A);
    private static ColorRgba Translucent(ColorRgba c, float alpha) => new(c.R, c.G, c.B, alpha);
    private static Color ToColor(ColorRgba c) => new(c.R, c.G, c.B, c.A);
}
