using System;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>WindowBuilder icon leaves — the launcher <see cref="TileElement"/> (icon + label + hover-grow/glow +
/// optional pin star) and the static <see cref="ImageElement"/>. Hover is registered to the renderer's
/// interaction ticker (polls the pointer against the cell rect); the builder stays sandbox-pure (hook null in
/// the sandbox → tiles render in their rest state). PNG icons are tracked on the token + reclaimed on destroy.</summary>
internal sealed partial class WindowBuilder
{
    // Dim/bright tile alpha — matches the IMGUI launcher's TileTint (0.62 rest / 1.0 hover).
    private const float TileRestAlpha = 0.62f;
    private const float TileHoverScale = 1.18f;
    private static readonly Color PinGold = new(0.91f, 0.77f, 0.35f, 1f);

    // Tile icon/label colours are computed LIVE from the (rebaked) MenuText so a theme switch re-tints them
    // correctly (the reskin + hover closures both read these, never a stale capture).
    private Color TileRest()   => new(_assets.MenuText.r, _assets.MenuText.g, _assets.MenuText.b, TileRestAlpha);
    private Color TileBright()  => new(_assets.MenuText.r, _assets.MenuText.g, _assets.MenuText.b, 1f);
    private Color PinMuted()    => new(_assets.MenuMuted.r, _assets.MenuMuted.g, _assets.MenuMuted.b, 0.7f);

    // Chrome-glyph resolver (star / mode_grid / …). Set by the renderer (Infrastructure has LauncherIcons; the
    // builder is sandbox-pure so it can't reference it). Null in the sandbox → those glyphs render blank.
    internal Func<string, byte[]?>? IconResolver;

    // Smooth icon load — high-res PNGs downscaled to 16–30px alias/pixelate WITHOUT mipmaps + trilinear (the
    // exact lesson from the IMGUI PluginIconCache). mipChain + Trilinear + Apply(updateMipmaps) = smooth.
    private Texture2D? LoadIcon(byte[]? png, WindowToken token)
    {
        if (png is not { Length: > 0 }) return null;
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true)
        {
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        try { if (!ImageConversion.LoadImage(tex, png)) { UnityEngine.Object.Destroy(tex); return null; } }
        catch { UnityEngine.Object.Destroy(tex); return null; }
        tex.Apply(updateMipmaps: true, makeNoLongerReadable: false);
        token.IconTextures.Add(tex);
        return tex;
    }

    private void BuildImage(ImageElement im, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Image", parent);
        UGuiPrimitives.SetPreferred(go, im.Width, im.Height);
        var raw = go.AddComponent<RawImage>(); raw.raycastTarget = false;
        var tex = LoadIcon(im.Png(), token);
        if (tex != null) raw.texture = tex;
    }

    // Atlas sub-sprite: a fixed-size RawImage whose uvRect selects one sub-region of a packed atlas PNG (the
    // DrawTextureWithTexCoords analog). Reuses LoadIcon (shared mipmap-smoothed load + token texture tracking);
    // RawImage.uvRect does the sub-rect blit natively. UV origin is bottom-left (Unity convention).
    private void BuildSprite(SpriteElement sp, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Sprite", parent);
        UGuiPrimitives.SetPreferred(go, sp.Width, sp.Height);
        var raw = go.AddComponent<RawImage>(); raw.raycastTarget = false;
        // Dedup: cells sharing one atlas byte[] reuse the single uploaded texture (no N× decode/upload).
        var bytes = sp.Atlas();
        Texture2D? tex = null;
        if (bytes is { Length: > 0 } && !token.AtlasCache.TryGetValue(bytes, out tex))
        {
            tex = LoadIcon(bytes, token);   // adds to IconTextures (single owner)
            if (tex != null) token.AtlasCache[bytes] = tex;
        }
        if (tex != null) raw.texture = tex;
        raw.uvRect = new UnityEngine.Rect(sp.Uv.X, sp.Uv.Y, sp.Uv.W, sp.Uv.H);
        // Dynamic sub-rect: re-pull UvFunc each poll so a recycled slot's icon tracks its backing data (the
        // static Uv above is the build-time seed / fallback). Diffing lives in the binding.
        if (sp.UvFunc != null) token.Sprites.Add(new SpriteBinding { Raw = raw, Uv = sp.UvFunc });
    }

    // Icon tile: a fixed-width cell whose icon+label are vertically CENTRED via a VerticalLayoutGroup (so the
    // block is centred regardless of cell height — the old absolute top/bottom anchoring left it top-aligned).
    // Hover scales the icon's localScale (visual only — does NOT reflow the layout) + brightens icon+label.
    // A transparent Image gives the whole cell one click target; an optional pin star sits at the icon's corner.
    private void BuildTile(TileElement tile, Transform parent, WindowToken token)
    {
        bool hasLabel = tile.Label != null;   // build the cell whenever a Label func is given (value may be live-empty)

        var cell = UGuiPrimitives.NewChild("Tile", parent);
        var cle = cell.AddComponent<LayoutElement>();
        cle.preferredWidth = tile.Width; cle.minWidth = tile.Width; cle.flexibleWidth = 0f;
        var hit = cell.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0f); hit.raycastTarget = true;
        var btn = cell.AddComponent<Button>(); btn.targetGraphic = hit; btn.transition = Selectable.Transition.None;
        var onClick = tile.OnClick; btn.onClick.AddListener((UnityAction)(() => onClick()));
        // Centring layout for icon (+ label). childControlWidth=false so the icon keeps its square size.
        var vlg = cell.AddComponent<VerticalLayoutGroup>();
        vlg.childAlignment = TextAnchor.MiddleCenter; vlg.spacing = 3f;
        vlg.padding = new RectOffset(0, 0, 4, 4);
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = false; vlg.childForceExpandHeight = false;
        var csf = cell.AddComponent<ContentSizeFitter>(); csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Icon is optional: a null/empty Icon() renders label-only (e.g. the launcher's "✕" close glyph).
        var icon = tile.Icon() is { Length: > 0 } ? BuildTileIcon(tile, cell, token) : null;
        var label = hasLabel ? BuildTileLabel(tile, cell, token) : null;

        // Hover: brighten icon+label + grow the icon (localScale → visual only, no reflow). Colours read live.
        var lblRef = label; var iconRef = icon;
        _registerHover?.Invoke(cell.GetComponent<RectTransform>(), on =>
        {
            if (iconRef != null) { iconRef.color = on ? TileBright() : TileRest(); iconRef.transform.localScale = on ? new Vector3(TileHoverScale, TileHoverScale, 1f) : Vector3.one; }
            if (lblRef != null) lblRef.color = on ? TileBright() : TileRest();
        });
        // Re-skin: re-apply the (rebaked) rest tint to icon+label in place on a theme change.
        token.ReskinActions.Add(() =>
        {
            if (icon != null) icon.color = TileRest();
            if (lblRef != null) { lblRef.color = TileRest(); lblRef.fontSize = Scaled(11); if (_assets.MenuFont != null) lblRef.font = _assets.MenuFont; }
        });

        if (tile.Pinned != null) BuildPinStar(cell, tile, token);
    }

    private RawImage BuildTileIcon(TileElement tile, GameObject cell, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Icon", cell.transform);
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = le.minWidth = tile.IconSize; le.preferredHeight = le.minHeight = tile.IconSize;
        var raw = go.AddComponent<RawImage>(); raw.raycastTarget = false; raw.color = TileRest();
        // Live icon: re-pull tile.Icon() each apply and swap the texture when the resolved bytes change. A
        // plugin icon that arrives AFTER this tile was built (plugins register async; the three launcher mode
        // layouts materialise at different times) then replaces the Icon("plugins") fallback instead of being
        // baked to it forever. byte[]-ref cache (shared with the atlas dedup) avoids re-decoding the same PNG.
        var binding = new IconBinding { Raw = raw, Bytes = tile.Icon, Load = png => LoadIcon(png, token), Cache = token.AtlasCache };
        binding.Apply();   // seed the initial texture now (and populate the cache)
        token.Icons.Add(binding);
        return raw;
    }

    private Text BuildTileLabel(TileElement tile, GameObject cell, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("TileLabel", cell.transform);
        float w = tile.Width - 6f;
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = le.minWidth = w; le.preferredHeight = 14f;
        var label = go.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(label, Scaled(11), TextAnchor.MiddleCenter, bold: false);
        ApplyMenuFont(label);
        // Single line; a too-long name is truncated with "..." (EllipsizeWidth) so it never wraps or spills into
        // the neighbouring tile.
        label.horizontalOverflow = HorizontalWrapMode.Overflow; label.verticalOverflow = VerticalWrapMode.Overflow;
        label.color = TileRest();
        token.Texts.Add(new TextBinding { C = label, TextFn = tile.Label!, EllipsizeWidth = w });   // non-null (hasLabel guards)
        // NOTE: deliberately NOT RegisterTextReskin (that forces alpha 1.0); the tile reskin closure in BuildTile
        // re-applies the dim rest tint + scaled size so the label keeps its 0.62 rest alpha after a theme change.
        return label;
    }

    // Animated brand logo (the old IMGUI DrawBrandLogo, in uGUI): an accent-tinted sparkle STAR with a soft
    // accent GLOW HALO behind it whose alpha + scale pulse. NO background button / border — the STAR glows.
    // Sandbox = static rest pulse (no ticker). Smooth via the LoadIcon mipmap fix.
    private void BuildBrandLogo(BrandLogoElement bl, Transform parent, WindowToken token)
    {
        var cell = UGuiPrimitives.NewChild("BrandLogo", parent);
        var le = cell.AddComponent<LayoutElement>();
        le.preferredWidth = le.minWidth = bl.Size; le.preferredHeight = le.minHeight = bl.Size;

        Color GlowColor(float a) => new(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, a);
        Color StarColor() => new(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 1f);

        // Glow halo behind the star (ignore-layout → free to extend past the cell, like a glow should), pulsing.
        var glowGo = UGuiPrimitives.NewChild("Glow", cell.transform);
        glowGo.AddComponent<LayoutElement>().ignoreLayout = true;
        var grt = glowGo.GetComponent<RectTransform>();
        grt.anchorMin = grt.anchorMax = grt.pivot = new Vector2(0.5f, 0.5f);
        grt.anchoredPosition = Vector2.zero; grt.sizeDelta = new Vector2(bl.Size * 1.7f, bl.Size * 1.7f);
        var glow = glowGo.AddComponent<RawImage>(); glow.raycastTarget = false; glow.color = GlowColor(0.4f);
        var glowTex = LoadIcon(bl.Glow(), token); if (glowTex != null) glow.texture = glowTex;

        // The crisp accent star on top.
        var sparkGo = UGuiPrimitives.NewChild("Sparkle", cell.transform);
        sparkGo.AddComponent<LayoutElement>().ignoreLayout = true;
        var srt = sparkGo.GetComponent<RectTransform>();
        srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(0.5f, 0.5f);
        srt.anchoredPosition = Vector2.zero; srt.sizeDelta = new Vector2(bl.Size, bl.Size);
        var spark = sparkGo.AddComponent<RawImage>(); spark.raycastTarget = false; spark.color = StarColor();
        var sparkTex = LoadIcon(bl.Sparkle(), token); if (sparkTex != null) spark.texture = sparkTex;

        token.ReskinActions.Add(() => { if (glow != null) glow.color = GlowColor(glow.color.a); if (spark != null) spark.color = StarColor(); });
        // Pulse: glow alpha 0.22..0.62 + scale 0.9..1.18 (the old GlowPulseSpeed feel). ONE closure, token-tracked
        // (cleanup on destroy) + registered to the ticker (per-frame drive).
        Action<float> pulse = p => { if (glow != null) { glow.color = GlowColor(0.22f + 0.4f * p); glow.rectTransform.localScale = Vector3.one * (0.9f + 0.28f * p); } };
        token.Pulses.Add(pulse);
        _registerPulse?.Invoke(pulse);
    }

    // ★/☆ pin overlay tucked at the icon's top-right corner (not the wide cell's), above the cell button
    // (a child graphic raycasts above its parent → the star click never also fires the cell's open).
    private void BuildPinStar(GameObject cell, TileElement tile, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Pin", cell.transform);
        go.AddComponent<LayoutElement>().ignoreLayout = true;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f); rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(14f, 14f);
        rt.anchoredPosition = new Vector2(tile.IconSize / 2f + 8f, -2f);   // just OUTSIDE the icon's top-right corner
        var raw = go.AddComponent<RawImage>(); raw.raycastTarget = true;
        var on = LoadIcon(IconResolver?.Invoke("star"), token);
        var off = LoadIcon(IconResolver?.Invoke("star_outline"), token);
        var pinnedFn = tile.Pinned!;
        bool init = false, last = false;
        token.Hovers.Add(new HoverBinding { Poll = () =>
        {
            if (raw == null || !raw.gameObject.activeInHierarchy) return;   // skip hidden tiles in the pool
            var p = pinnedFn();
            if (init && p == last) return;   // diff: only touch the graphic on change
            raw.texture = p ? on : off; raw.color = p ? PinGold : PinMuted();
            last = p; init = true;
        } });
        var btn = go.AddComponent<Button>(); btn.targetGraphic = raw; btn.transition = Selectable.Transition.None;
        var toggle = tile.OnTogglePin;
        if (toggle != null) btn.onClick.AddListener((UnityAction)(() => toggle()));
    }
}
