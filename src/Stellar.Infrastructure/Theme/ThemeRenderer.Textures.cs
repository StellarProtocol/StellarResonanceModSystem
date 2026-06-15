using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.Infrastructure.Theme;

/// <summary>
/// Static texture bakers used by <see cref="ThemeRenderer.BakeTextures"/>.
/// Split into a sibling partial so the main file stays well under the
/// 500-LoC ceiling enforced by <c>tools/check-standards.sh</c>. Each baker
/// returns a procedurally-generated <see cref="Texture2D"/> with explicit
/// <c>filterMode</c> + <c>wrapMode</c> set per ux-ui MODE: review minor
/// "explicit texture filter/wrap" guidance.
/// </summary>
internal sealed partial class ThemeRenderer
{
    /// <summary>
    /// Destroys every baked Texture2D this renderer owns. Called from
    /// <c>OnNamedThemeChanged</c> before a re-bake, and from the host's
    /// <c>Unload</c> path so a framework reload doesn't leave orphan textures
    /// in the Unity scene. Idempotent — null fields are skipped, and the
    /// fields are nulled after destroy so a subsequent call is a no-op.
    /// </summary>
    internal void DestroyBakedTextures()
    {
        DestroyAndNull(ref _accentTex);
        DestroyAndNull(ref _bannerBgTex);
        DestroyAndNull(ref _slantCapTex);
        DestroyAndNull(ref _railGlowTex);
        DestroyAndNull(ref _dividerTex);
        DestroyAndNull(ref _titleRowBgTex);
        DestroyAndNull(ref _trackerDividerTex);
        DestroyAndNull(ref _trackerIconTex);
        DestroyAndNull(ref _trackerIconGlowTex);
        DestroyAndNull(ref _glassMenuBgTex);
        DestroyChromeStyleTextures();
        // GUIStyles don't own GPU resources; null the cached styles so they
        // rebuild against the new font on preset / scale switch.
        _glassMenuBgStyle    = null;
        _glassMenuTitleStyle = null;
        _glassMenuCloseStyle = null;
    }

    private static void DestroyAndNull(ref Texture2D? tex)
    {
        if (tex == null) return;
        UnityEngine.Object.Destroy(tex);
        tex = null;
    }

    private static Texture2D MakeTexture(ColorRgba colour)
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags  = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp,
        };
        tex.SetPixel(0, 0, ToUnity(colour));
        tex.Apply(updateMipmaps: false);
        return tex;
    }

    /// <summary>
    /// Vertical mint gradient for the Party banner: <c>top</c> at row 0
    /// (which Unity stores at texel y = height-1) to <c>bottom</c> at the
    /// last row. Mirrors panel-styles.html § <c>.pw-party .banner</c>:
    /// <c>linear-gradient(to bottom, #6efad4, #4ad9b8)</c>.
    /// </summary>
    private static Texture2D MakeBannerBgTexture(int height, ColorRgba top, ColorRgba bottom)
    {
        var tex = new Texture2D(1, height, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags  = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp,
        };
        for (int ty = 0; ty < height; ty++)
        {
            // screen-y from top, in pixels.
            float screenY = (height - 1) - ty;
            float t = screenY / (float)(height - 1);   // 0 at top, 1 at bottom
            var c = new Color(
                Mathf.Lerp(top.R, bottom.R, t),
                Mathf.Lerp(top.G, bottom.G, t),
                Mathf.Lerp(top.B, bottom.B, t),
                Mathf.Lerp(top.A, bottom.A, t));
            tex.SetPixel(0, ty, c);
        }
        tex.Apply(updateMipmaps: false);
        return tex;
    }

    /// <summary>
    /// Builds the slant-cap texture by super-sampling at 4×: for each output
    /// texel, take 16 sub-samples on a 4×4 grid; alpha = (mint sub-samples / 16).
    /// This antialiases the diagonal so the cap doesn't look pixelated when
    /// drawn at native size. Boundary uses <c>&gt;=</c> (not strict <c>&gt;</c>)
    /// to close the 1-pixel join hairline against the main banner.
    /// </summary>
    private static Texture2D MakeSlantCapTextureSupersampled(int width, int height, ColorRgba accent)
    {
        const int Factor = 4;
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags  = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp,
        };
        var mint = ToUnity(accent);
        var pixels = new Color[width * height];

        int superW = width  * Factor;     // 56
        int superH = height * Factor;     // 72

        for (int ty = 0; ty < height; ty++)
        for (int tx = 0; tx < width; tx++)
        {
            int hits = 0;
            for (int sy = 0; sy < Factor; sy++)
            for (int sx = 0; sx < Factor; sx++)
            {
                // Map (tx,ty) + sub-pixel offset into super-sample space.
                int superX  = tx * Factor + sx;
                int superTy = ty * Factor + sy;
                // screen-y from top, in super-sample pixels.
                float screenY = (superH - 1) - superTy;
                // diagonal: screenY * superW >= superX * superH  ⇒  mint.
                // `>=` instead of `>` closes the join hairline.
                if (screenY * superW >= superX * superH) hits++;
            }
            float alpha = hits / (float)(Factor * Factor);
            pixels[ty * width + tx] = new Color(mint.r, mint.g, mint.b, mint.a * alpha);
        }
        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false);
        return tex;
    }

    /// <summary>
    /// Builds the rail glow texture: full mint at column 0 fading to fully
    /// transparent at the right edge. Drawn behind the solid 3px rail; the
    /// falloff approximates the CSS box-shadow softness.
    /// </summary>
    private static Texture2D MakeRailGlowTexture(int width, ColorRgba accent)
    {
        var tex = new Texture2D(width, 1, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags  = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp,
        };
        var pixels = new Color[width];
        for (int x = 0; x < width; x++)
        {
            float t = x / (float)(width - 1);          // 0 at left, 1 at right
            float alpha = (1f - t) * 0.5f;             // peak 50% (matches CSS .5 box-shadow)
            pixels[x] = new Color(accent.R, accent.G, accent.B, alpha);
        }
        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false);
        return tex;
    }

    /// <summary>
    /// Party divider gradient (under the banner): full mint at the left,
    /// ~40% alpha at 80% across, transparent at the right. Mirrors
    /// <c>linear-gradient(to right, #5fe8c5, rgba(95,232,197,.4) 80%, transparent)</c>.
    /// </summary>
    private static Texture2D MakeDividerTexture(int width, ColorRgba accent)
    {
        var tex = new Texture2D(width, 1, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags  = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp,
        };
        var pixels = new Color[width];
        for (int x = 0; x < width; x++)
        {
            float t = x / (float)(width - 1);
            float alpha = t <= 0.8f
                ? Mathf.Lerp(1.0f, 0.4f, t / 0.8f)            // 0..80% : 1.0 → 0.4
                : Mathf.Lerp(0.4f, 0.0f, (t - 0.8f) / 0.2f);  // 80..100%: 0.4 → 0
            pixels[x] = new Color(accent.R, accent.G, accent.B, alpha);
        }
        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false);
        return tex;
    }

    /// <summary>
    /// Tracker title-row background: horizontal gradient
    /// <c>linear-gradient(to right, rgba(20,28,38,.55), rgba(20,28,38,.15) 80%, transparent)</c>.
    /// </summary>
    private static Texture2D MakeTitleRowBgTexture(int width, ColorRgba left, ColorRgba midAt80)
    {
        var tex = new Texture2D(width, 1, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags  = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp,
        };
        var pixels = new Color[width];
        for (int x = 0; x < width; x++)
        {
            float t = x / (float)(width - 1);
            // 0..80% : left → midAt80; 80..100% : midAt80 → transparent.
            if (t <= 0.8f)
            {
                float u = t / 0.8f;
                pixels[x] = new Color(
                    Mathf.Lerp(left.R, midAt80.R, u),
                    Mathf.Lerp(left.G, midAt80.G, u),
                    Mathf.Lerp(left.B, midAt80.B, u),
                    Mathf.Lerp(left.A, midAt80.A, u));
            }
            else
            {
                float u = (t - 0.8f) / 0.2f;
                pixels[x] = new Color(
                    midAt80.R,
                    midAt80.G,
                    midAt80.B,
                    Mathf.Lerp(midAt80.A, 0f, u));
            }
        }
        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false);
        return tex;
    }

    /// <summary>
    /// Tracker divider gradient: solid mint 0..30%, fading to transparent
    /// at 100%. Mirrors panel-styles.html § <c>.pw-tracker .grad-divider</c>:
    /// <c>linear-gradient(to right, #5fe8c5 0%, #5fe8c5 30%, transparent 100%)</c>.
    /// Distinct from <see cref="MakeDividerTexture"/> which fades from 0%.
    /// </summary>
    private static Texture2D MakeTrackerDividerTexture(int width, ColorRgba accent)
    {
        var tex = new Texture2D(width, 1, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags  = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp,
        };
        var pixels = new Color[width];
        for (int x = 0; x < width; x++)
        {
            float t = x / (float)(width - 1);
            float alpha = t <= 0.3f
                ? 1.0f                                       // 0..30% : solid mint
                : Mathf.Lerp(1.0f, 0.0f, (t - 0.3f) / 0.7f); // 30..100%: 1.0 → 0
            pixels[x] = new Color(accent.R, accent.G, accent.B, alpha);
        }
        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false);
        return tex;
    }

    /// <summary>
    /// Tracker title-row icon: mint-coloured disc, baked at 2× super-sample
    /// (<paramref name="size"/>×2 → <paramref name="size"/>) for smooth edge
    /// antialiasing via signed-distance alpha falloff. Mirrors panel-styles.html
    /// § <c>.pw-tracker .title-row .left .icon</c>:
    /// <c>width:12px; height:12px; border-radius:50%; background:#5fe8c5</c>.
    /// Drawn at <c>size</c>×<c>size</c> with Bilinear filtering for the final
    /// downscale.
    /// </summary>
    private static Texture2D MakeTrackerIconTexture(int size, ColorRgba accent)
    {
        const int Factor = 2;
        int texW = size * Factor;
        var tex = new Texture2D(texW, texW, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags  = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp,
        };
        float cx = texW / 2f;
        float cy = texW / 2f;
        // Radius leaves a 1-px (super-sample) border for the AA falloff.
        float r  = (texW / 2f) - 1f;
        var pixels = new Color[texW * texW];
        for (int ty = 0; ty < texW; ty++)
        for (int tx = 0; tx < texW; tx++)
        {
            float dx = (tx + 0.5f) - cx;
            float dy = (ty + 0.5f) - cy;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            float alpha = Mathf.Clamp01(r - dist);
            pixels[ty * texW + tx] = new Color(accent.R, accent.G, accent.B, accent.A * alpha);
        }
        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false);
        return tex;
    }

    /// <summary>
    /// Tracker title-row icon soft glow: same disc shape as
    /// <see cref="MakeTrackerIconTexture"/> but with low peak alpha and the
    /// alpha falloff stretched across the full radius — approximates the CSS
    /// <c>box-shadow: 0 0 4px rgba(95,232,197,.6)</c>. Drawn behind the solid
    /// icon at a larger size.
    /// </summary>
    private static Texture2D MakeTrackerIconGlowTexture(int size, ColorRgba accent)
    {
        const int Factor = 2;
        int texW = size * Factor;
        var tex = new Texture2D(texW, texW, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags  = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp,
        };
        float cx = texW / 2f;
        float cy = texW / 2f;
        float r  = texW / 2f;
        var pixels = new Color[texW * texW];
        for (int ty = 0; ty < texW; ty++)
        for (int tx = 0; tx < texW; tx++)
        {
            float dx = (tx + 0.5f) - cx;
            float dy = (ty + 0.5f) - cy;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            // Quadratic falloff from peak alpha at center to 0 at the edge.
            float t = Mathf.Clamp01(dist / r);
            float alpha = (1f - t) * (1f - t) * 0.35f;
            pixels[ty * texW + tx] = new Color(accent.R, accent.G, accent.B, accent.A * alpha);
        }
        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false);
        return tex;
    }
}
