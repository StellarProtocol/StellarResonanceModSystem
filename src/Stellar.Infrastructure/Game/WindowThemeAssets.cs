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
    private const int CapsuleRadius = CapsuleGeometry.Radius;

    // Swatch: 3-radius rounded square (theme-editor colour chips). Slice border = radius + 1.
    private const int SwatchTexSize = 16;
    private const int SwatchRadius = 3;

    private Texture2D? _frameTex, _btnTex, _btnAccentTex, _capsuleTex, _panelTex, _hgradTex, _titleTex, _swatchTex, _btnGlassTex, _cdOutlineTex;

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
    public Sprite? CdOutline { get; private set; }      // white rounded ring (radius 8, matches PanelBg) — CooldownTile border, tinted a darkened accent at use-site

    // Rounded white bubble/card sprites keyed by corner radius (px). Theme-INDEPENDENT (white, tinted to the
    // bubble's fill at the use-site, exactly like SwatchBg), baked on demand and KEPT across re-bakes — a
    // BubbleElement's Image holds a direct reference, so destroying these on a theme change would strand it.
    private readonly System.Collections.Generic.Dictionary<int, (Texture2D tex, Sprite sprite)> _bubbleSprites = new();

    /// <summary>A rounded white 9-slice chip for a <c>BubbleElement</c> background, tinted to the bubble's fill at
    /// the use-site. <paramref name="radius"/> &lt;= 0 returns the shared 3-radius <see cref="SwatchBg"/> chip; a
    /// positive radius bakes (once, cached) a rounder sprite at that corner radius. Main-thread only (bakes a
    /// Texture2D). Null only before the neutral sprites are baked (a pre-bake sandbox) — the caller falls back to a
    /// plain tinted rect.</summary>
    public Sprite? BubbleBg(int radius)
    {
        if (radius <= 0) return SwatchBg;   // 0 → the shared editor chip (matches SelectableElement's rounding)
        if (_bubbleSprites.TryGetValue(radius, out var e))
        {
            if (Alive(e)) return e.sprite;
            Evict(_bubbleSprites, radius);
            // The left-cut sprite shares this texture → it died with it; drop it so BubbleBgLeft re-cuts the new one.
            if (_bubbleLeftSprites.Remove(radius, out var dead) && dead != null) Object.Destroy(dead);
        }
        // size = 2*radius + a small centre strip so the 9-slice corners fit; slice border = radius + 1 (as SwatchBg).
        var size = radius * 2 + 4;
        var tex = RoundedTextureBaker.Rounded(size, radius, new ColorRgba(1f, 1f, 1f, 1f));
        var sprite = Sliced(tex, radius + 1);
        _bubbleSprites[radius] = (tex, sprite);
        return sprite;
    }

    // Left-rounded variants (corners on the LEFT only, straight right edge), keyed by radius. A second sprite over the
    // SAME cached BubbleBg texture (no extra bake/texture) — same lifetime rule (kept across re-bakes).
    private readonly System.Collections.Generic.Dictionary<int, Sprite> _bubbleLeftSprites = new();

    /// <summary>A white 9-slice chip rounded on its LEFT corners only (right edge straight), matching
    /// <see cref="BubbleBg"/> at the same <paramref name="radius"/> (&gt; 0). Cut from BubbleBg's texture: the left
    /// r+2 columns, border (r+1, r+1, 0, r+1). For a share wash flush inside a rounded tile (AccentRowElement
    /// CornerRadius, 2.33.0). Main-thread only; null before the neutral sprites are baked.</summary>
    public Sprite? BubbleBgLeft(int radius)
    {
        if (radius <= 0) return null;
        if (BubbleBg(radius) == null) return null;   // first: validates the shared texture (evicts our sprite if it died)
        if (_bubbleLeftSprites.TryGetValue(radius, out var s))
        {
            if (s != null) return s;
            _bubbleLeftSprites.Remove(radius);   // destroyed behind the cache → re-cut below
        }
        var tex = _bubbleSprites[radius].tex;
        var b = radius + 1;
        s = Sprite.Create(tex, new UnityEngine.Rect(0f, 0f, b + 1, tex.height), new Vector2(0.5f, 0.5f),
            pixelsPerUnit: 100f, extrude: 0, meshType: SpriteMeshType.FullRect, border: new Vector4(b, b, 0f, b));
        s.hideFlags = HideFlags.HideAndDontSave;
        _bubbleLeftSprites[radius] = s;
        return s;
    }

    // White 1-px rounded RINGS (transparent interior) for BubbleElement.OutlineColorFn, keyed by the same radius as the
    // bubble's fill sprite so the ring hugs its exact edge. Same lifetime rule as _bubbleSprites (kept across re-bakes).
    private readonly System.Collections.Generic.Dictionary<int, (Texture2D tex, Sprite sprite)> _bubbleOutlines = new();

    /// <summary>A white 1-px rounded ring matching <see cref="BubbleBg"/> at the same <paramref name="radius"/>
    /// (&lt;= 0 → the 3-radius chip rounding), tinted at the use-site. Baked once per radius; main-thread only.</summary>
    public Sprite? BubbleOutline(int radius)
    {
        var r = radius <= 0 ? SwatchRadius : radius;
        if (_bubbleOutlines.TryGetValue(r, out var e)) { if (Alive(e)) return e.sprite; Evict(_bubbleOutlines, r); }
        var size = r * 2 + 4;
        var tex = RoundedTextureBaker.RoundedBordered(size, r, 1, new ColorRgba(0f, 0f, 0f, 0f), new ColorRgba(1f, 1f, 1f, 1f));
        var sprite = Sliced(tex, r + 1);
        _bubbleOutlines[r] = (tex, sprite);
        return sprite;
    }

    // Half-pixel rings (the house 1.5 px selection ring), keyed by (radius, width in half-px). Same lifetime rule.
    private readonly System.Collections.Generic.Dictionary<int, (Texture2D tex, Sprite sprite)> _halfPxRings = new();

    /// <summary>A white rounded ring baked at 2× (corner <paramref name="radius"/>×2, <paramref name="halfPx"/>
    /// texels wide). Draw it Sliced with <c>Image.pixelsPerUnitMultiplier = 2</c>: it then lands at
    /// <paramref name="radius"/> px corners and <paramref name="halfPx"/>/2 px width (3 → 1.5 px), matching a
    /// <see cref="BubbleBg"/> fill of the same radius. A plain 1-px <see cref="BubbleOutline"/> can't do half
    /// pixels. Baked once per key; main-thread only.</summary>
    public Sprite? BubbleRingHalfPx(int radius, int halfPx)
    {
        var key = radius * 64 + halfPx;
        if (_halfPxRings.TryGetValue(key, out var e)) { if (Alive(e)) return e.sprite; Evict(_halfPxRings, key); }
        var r2 = System.Math.Max(1, radius) * 2;
        var tex = RoundedTextureBaker.RoundedBordered(r2 * 2 + 4, r2, halfPx, new ColorRgba(0f, 0f, 0f, 0f), new ColorRgba(1f, 1f, 1f, 1f));
        var sprite = Sliced(tex, r2 + 1);
        _halfPxRings[key] = (tex, sprite);
        return sprite;
    }

    /// <summary>BubbleElement.OutlineWidth (2.33.0): a ring <paramref name="widthPx"/> wide, snapped to 0.5 px and
    /// clamped to [0.5, corner radius] (a ring wider than the corner would spill into the 9-slice centre strip and
    /// stretch). Reuses the <see cref="BubbleRingHalfPx"/> cache, so it MUST be drawn Sliced with
    /// <c>pixelsPerUnitMultiplier = 2</c>. <paramref name="radius"/> &lt;= 0 → the 3-radius chip rounding, matching
    /// <see cref="BubbleBg"/>(0). Main-thread only.</summary>
    public Sprite? BubbleOutlineWidth(int radius, float widthPx)
    {
        var r = radius <= 0 ? SwatchRadius : radius;
        // halfPx < 64 keeps BubbleRingHalfPx's radius*64+halfPx key collision-free.
        var halfPx = System.Math.Clamp((int)System.Math.Round(widthPx * 2f), 1, System.Math.Min(r * 2, 63));
        return BubbleRingHalfPx(r, halfPx);
    }
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

    /// <summary>Process-shared glyph-complete overlay font (Latin + CJK + Thai via the OS-font
    /// fallback chain), for static text paths that have no <see cref="WindowThemeAssets"/> instance
    /// (e.g. the native-UI-host label builders). Null only when OS-font resolution failed entirely,
    /// in which case the caller keeps its builtin-Arial attempt. Main-thread only (lazy-inits the font).</summary>
    public static Font? SharedMenuFont { get { EnsureFont(); return _menuFont; } }

    // OS-font fallback chain — ordering rules + the 2.21.0 regression pin live in OverlayFontFamilies (Unity-free).
    private static readonly string[] FontFamilies = OverlayFontFamilies.Chain;

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
        if (CdOutline == null)   // CooldownTile border ring — white/tint-through, radius 8 (same shape as PanelBg)
        {
            _cdOutlineTex = RoundedTextureBaker.Rounded(16, 8, new ColorRgba(1f, 1f, 1f, 1f));
            CdOutline = Sliced(_cdOutlineTex, 8);
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
        Drop(ref _cdOutlineTex, () => CdOutline, s => CdOutline = s);
        // Left-cut sprites first: they are sub-rect sprites over the _bubbleSprites textures destroyed just below.
        foreach (var s in _bubbleLeftSprites.Values) if (s != null) Object.Destroy(s);
        _bubbleLeftSprites.Clear();
        DestroyPairs(_bubbleSprites);
        DestroyPairs(_bubbleOutlines);
        DestroyPairs(_halfPxRings);
    }

    private static void DestroyPairs(System.Collections.Generic.Dictionary<int, (Texture2D tex, Sprite sprite)> d)
    {
        foreach (var e in d.Values)
        {
            if (e.sprite != null) Object.Destroy(e.sprite);
            if (e.tex != null) Object.Destroy(e.tex);
        }
        d.Clear();
    }

    // Liveness of a cached (tex, sprite) pair. Unity's overloaded == reports a DESTROYED object as null, so this catches a
    // pair killed behind the cache's back (an explicit Destroy, or a scene-switch Resources.UnloadUnusedAssets sweep if
    // the HideAndDontSave flag were ever lost) — handing a dead sprite to a new Image renders nothing at best.
    private static bool Alive((Texture2D tex, Sprite sprite) e) => e.sprite != null && e.tex != null;

    // Evict a dead pair (destroying whichever half survived) so the caller re-bakes it.
    private static void Evict(System.Collections.Generic.Dictionary<int, (Texture2D tex, Sprite sprite)> d, int key)
    {
        if (!d.TryGetValue(key, out var e)) return;
        if (e.sprite != null) Object.Destroy(e.sprite);
        if (e.tex != null) Object.Destroy(e.tex);
        d.Remove(key);
    }

    // White sprite with the TOP two corners rounded (radius r), bottom square. 4× supersampled AA.
    // Unity Texture2D y=0 is the bottom row, so "top" corners are the high-y rows.
    private Texture2D BakeTopRounded(int size, int r)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true)
        { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
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
        tex.SetPixels(px); tex.Apply(true);
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

/// <summary>
/// The OS-font family chain the legacy-<c>Text</c> overlay font is built from
/// (<c>Font.CreateDynamicFontFromOSFont(string[], int)</c> in <see cref="WindowThemeAssets"/>). Kept in this
/// Unity-free type (deliberately in WindowThemeAssets.cs, which the UI sandbox symlinks) so the ordering regression pin (<c>FontFamilyOrderTests</c>) can load it outside the game.
/// </summary>
internal static class OverlayFontFamilies
{
    // CreateDynamicFontFromOSFont renders each glyph from the FIRST listed family that provides it, so the
    // chain must cover every UI language Stellar localizes into (i18n P0): Latin (Noto Sans) → CJK for ja/zh
    // (Noto Sans CJK JP/SC) → Thai for th (Noto Sans Thai/Thai UI) → the DejaVu/Liberation tail that covers the
    // Proton box → Arial (Unity's always-synthesised last resort) → Hangul for ko (Noto Sans CJK KR / Malgun
    // Gothic — Windows' Korean UI face; Yu Gothic/Meiryo carry no Hangul). Indonesian (id) and Filipino (fil)
    // are Latin, covered by Noto Sans. Without the Thai families, th text tofu'd — the glyph-coverage gate
    // (i18n Task 0) added them.
    //
    // ORDERING RULE: a family added for a new script that ALSO covers Latin (Malgun Gothic, the Noto CJK faces)
    // must never precede the Latin families ("Noto Sans", "DejaVu Sans", "Liberation Sans", "Arial") — append it
    // AFTER "Arial" so it only supplies glyphs no earlier family has. 2.21.0 pre-release inserted the Hangul
    // families before the Latin tail and every English glyph on the owner's Proton prefix (no Noto/DejaVu;
    // Liberation Sans + Arial + malgun.ttf) switched from Liberation Sans to Malgun Gothic; on real Windows
    // Malgun would likewise have replaced Arial. Pinned by FontFamilyOrderTests (do not weaken).
    internal static readonly string[] Chain =
    {
        "Noto Sans", "NotoSans",
        "Noto Sans CJK JP", "Noto Sans CJK SC",
        "Noto Sans Thai", "Noto Sans Thai UI",
        "DejaVu Sans", "Liberation Sans", "Arial",
        "Noto Sans CJK KR", "Malgun Gothic",
    };
}

/// <summary>
/// Unity-free geometry of the <see cref="WindowThemeAssets.Capsule"/> sprite — a separate class (like
/// <see cref="OverlayFontFamilies"/>) so tests can call it without loading <c>UnityEngine.CoreModule</c>.
/// </summary>
internal static class CapsuleGeometry
{
    /// <summary>Corner radius (= 9-slice border) of the capsule sprite, in texels.</summary>
    internal const int Radius = 7;

    /// <summary>
    /// <c>Image.pixelsPerUnitMultiplier</c> that makes the capsule sprite draw a full CIRCLE in a
    /// <paramref name="side"/>-px square. Sprites are baked at <c>pixelsPerUnit: 100</c> and
    /// <c>Canvas.referencePixelsPerUnit</c> is never set (default 100), so 1 texel = 1 canvas unit and a sliced
    /// border draws at <c>border / multiplier</c> units → multiplier = <c>Radius / (side/2)</c> scales the
    /// 7-texel corners to <c>side/2</c> each, so the arcs meet with no straight edge. Without it any knob larger
    /// than <c>2 × Radius</c> (14 px) keeps 7 px corners = a rounded SQUARE (Dungeon Hub, 22 px).
    /// </summary>
    internal static float CircleMultiplier(float side) =>
        side > 0f ? Radius / (side * 0.5f) : 1f;
}
