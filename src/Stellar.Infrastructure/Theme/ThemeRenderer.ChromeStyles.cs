using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Theme;

/// <summary>
/// Chrome control styling — skins <c>GUI.skin.button</c> + the vertical scrollbar so
/// every GlassMenu panel matches the game-UI mood instead of Unity's grey defaults.
/// The active look comes from <see cref="IChromeStyle"/> (user-selectable, default
/// Outline button + ThumbOnly scrollbar); textures for every variant are baked per
/// theme + FontScale and rebuilt via the destroy-and-reinit path. Button label colour
/// is set here per style (NOT by the chrome's per-window text pass, which deliberately
/// skips the button so Filled can use a dark label on the accent fill).
/// </summary>
internal sealed partial class ThemeRenderer
{
    private readonly IChromeStyle _chromeStyle;

    // Button backgrounds (normal/hover) per style.
    private Texture2D? _btnOutlineN, _btnOutlineH;
    private Texture2D? _btnFilledN,  _btnFilledH;
    private Texture2D? _btnGlassN,   _btnGlassH;
    // Scrollbar thumbs + faint track.
    private Texture2D? _scrollThumbAccent, _scrollThumbMuted, _scrollTrackFaint;
    private Texture2D? _chromeEmptyTex;

    private void BakeChromeStyleTextures()
    {
        var a = Colors.MenuAccent;
        ColorRgba A(float alpha) => new(a.R, a.G, a.B, alpha);
        var white = new ColorRgba(1f, 1f, 1f, 1f);
        ColorRgba W(float alpha) => new(white.R, white.G, white.B, alpha);

        _btnOutlineN = MakeRoundedBorderedTexture(16, 4, 1, A(0f),    A(0.70f));
        _btnOutlineH = MakeRoundedBorderedTexture(16, 4, 1, A(0.14f), A(1.0f));
        _btnFilledN  = MakeRoundedTexture(16, 4, a);
        _btnFilledH  = MakeRoundedTexture(16, 4, LightenToward(a, 0.20f));
        _btnGlassN   = MakeRoundedBorderedTexture(16, 4, 1, W(0.06f), A(0.30f));
        _btnGlassH   = MakeRoundedBorderedTexture(16, 4, 1, W(0.12f), A(0.55f));

        _scrollThumbAccent = MakeRoundedTexture(8, 3, A(0.55f));
        _scrollThumbMuted  = MakeRoundedTexture(8, 3, new ColorRgba(Colors.MenuMuted.R, Colors.MenuMuted.G, Colors.MenuMuted.B, 0.80f));
        _scrollTrackFaint  = MakeRoundedTexture(8, 3, W(0.07f));
        _chromeEmptyTex    = MakeTexture(new ColorRgba(0f, 0f, 0f, 0f));
    }

    private void DestroyChromeStyleTextures()
    {
        DestroyAndNull(ref _btnOutlineN); DestroyAndNull(ref _btnOutlineH);
        DestroyAndNull(ref _btnFilledN);  DestroyAndNull(ref _btnFilledH);
        DestroyAndNull(ref _btnGlassN);   DestroyAndNull(ref _btnGlassH);
        DestroyAndNull(ref _scrollThumbAccent);
        DestroyAndNull(ref _scrollThumbMuted);
        DestroyAndNull(ref _scrollTrackFaint);
        DestroyAndNull(ref _chromeEmptyTex);
    }

    /// <summary>
    /// Apply the selected button + scrollbar skin to <c>GUI.skin</c>. Called at the tail
    /// of ApplyGuiSkinDefaults (init + every theme/scale/style change).
    /// </summary>
    private void ApplyChromeStyles()
    {
        if (_btnOutlineN == null) BakeChromeStyleTextures();
        ApplyButtonStyle();
        ApplyScrollbarStyle();
    }

    private void ApplyButtonStyle()
    {
        var b = GUI.skin.button;
        Texture2D? n, h;
        Color text;
        switch (_chromeStyle.ButtonStyle)
        {
            case MenuButtonStyle.Filled:
                n = _btnFilledN; h = _btnFilledH; text = ToUnity(Colors.MenuBackground);
                break;
            case MenuButtonStyle.Glass:
                n = _btnGlassN; h = _btnGlassH; text = ToUnity(Colors.MenuText);
                break;
            default: // Outline
                n = _btnOutlineN; h = _btnOutlineH; text = ToUnity(Colors.MenuText);
                break;
        }
        b.normal.background = n; b.focused.background = n;
        b.hover.background  = h; b.active.background  = h;
        b.border = new RectOffset(4, 4, 4, 4);
        b.normal.textColor = text; b.hover.textColor = text; b.active.textColor = text; b.focused.textColor = text;
        // Optically centre the label: MiddleCenter leaves the ink low. The previous
        // -3 was tuned against the sandbox (Arial) and over-corrected with the real
        // in-game font (text sat too high). -1 reads centred in-game; re-confirm in
        // the game, not the sandbox, since the two fonts differ.
        b.alignment = TextAnchor.MiddleCenter;
        b.contentOffset = new Vector2(0f, -1f);
    }

    private void ApplyScrollbarStyle()
    {
        var thumbOnly = _chromeStyle.ScrollbarStyle == MenuScrollbarStyle.ThumbOnly;

        var sb = GUI.skin.verticalScrollbar;
        sb.normal.background = thumbOnly ? _chromeEmptyTex : _scrollTrackFaint;
        sb.border = thumbOnly ? new RectOffset(0, 0, 0, 0) : new RectOffset(3, 3, 3, 3);
        sb.fixedWidth = 7f;

        var thumb = GUI.skin.verticalScrollbarThumb;
        thumb.normal.background = thumbOnly ? _scrollThumbAccent : _scrollThumbMuted;
        thumb.border = new RectOffset(3, 3, 3, 3);
        thumb.fixedWidth = thumbOnly ? 4f : 5f;

        var up = GUI.skin.verticalScrollbarUpButton;
        up.normal.background = _chromeEmptyTex; up.fixedHeight = 0f;
        var down = GUI.skin.verticalScrollbarDownButton;
        down.normal.background = _chromeEmptyTex; down.fixedHeight = 0f;
    }
}
