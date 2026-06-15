using System;
using Stellar.Abstractions.Domain;
using UnityEngine;
using UnityRect = UnityEngine.Rect;

namespace Stellar.Infrastructure.Theme;

/// <summary>
/// Phase 9b GlassMenu chrome — light/dark gradient panel with subtle 1 px
/// border, inline title text, ✕ close glyph, and a drag handle on the
/// title bar minus the close hit area. Used by tool / menu windows that
/// want the game's Profile / Modules / Inventory visual identity.
/// </summary>
/// <remarks>
/// Gradient bg is baked once per preset by
/// <see cref="BakeGlassMenuTextures"/> at <see cref="Initialise"/> time
/// and rebuilt on theme / FontScale change via the existing destroy +
/// re-init loop. The bake carries rounded corners in its alpha channel;
/// the draw uses <see cref="GUI.Box"/> with a 9-slice <c>GUIStyle.border</c>
/// so the corner curve preserves at any window dimension.
/// </remarks>
internal sealed partial class ThemeRenderer
{
    // Locked dimensions — keep in lockstep with the design spec.
    private const int GlassMenuTitleBarHeight  = 28;
    private const int GlassMenuTitlePadX       = 16;
    private const int GlassMenuTitleFontSize   = 14;
    private const int GlassMenuTitleMinFontSize = 7;   // floor when auto-shrinking to fit a narrow window
    private const int GlassMenuCloseHitWidth   = 28;
    private const int GlassMenuCloseFontSize   = 14;

    private GUIStyle? _glassMenuBgStyle;
    private GUIStyle? _glassMenuTitleStyle;
    private GUIStyle? _glassMenuCloseStyle;

    // Shared 1×1 white texture for accent/border lines. HideAndDontSave so it
    // survives scene loads. Created lazily in EnsureSettingsResources.
    private Texture2D? _settingsWhiteTex;

    private void EnsureSettingsResources()
    {
        if (_settingsWhiteTex is null)
        {
            _settingsWhiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false)
            {
                hideFlags  = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp,
            };
            _settingsWhiteTex.SetPixel(0, 0, Color.white);
            _settingsWhiteTex.Apply(updateMipmaps: false);
        }
    }

    /// <summary>
    /// Single-call wire-up invoked from <c>BuildGuiStyles</c> in the main
    /// renderer file. Keeps that file's diff to one line and bundles the
    /// three GlassMenu style builders + bake call together for clarity.
    /// </summary>
    private void WireGlassMenuStyles()
    {
        _glassMenuBgStyle    = BuildGlassMenuBgStyle();
        _glassMenuTitleStyle = BuildGlassMenuTitleStyle();
        _glassMenuCloseStyle = BuildGlassMenuCloseStyle();
    }

    /// <summary>
    /// Builds the 9-sliced background style. Border = <see cref="GlassMenuRadius"/>
    /// on all four sides so the corner curve baked into the alpha channel of
    /// <c>_glassMenuBgTex</c> stretches correctly. <c>normal.textColor.a = 0</c>
    /// guards against accidental phantom label text if a caller passes content.
    /// </summary>
    private GUIStyle BuildGlassMenuBgStyle()
    {
        var style = new GUIStyle
        {
            border   = new RectOffset(GlassMenuRadius, GlassMenuRadius, GlassMenuRadius, GlassMenuRadius),
            padding  = new RectOffset(0, 0, 0, 0),
            margin   = new RectOffset(0, 0, 0, 0),
            overflow = new RectOffset(0, 0, 0, 0),
        };
        style.normal.background = _glassMenuBgTex;
        style.normal.textColor  = new Color(0f, 0f, 0f, 0f);
        return style;
    }

    private GUIStyle BuildGlassMenuTitleStyle()
    {
        var style = new GUIStyle
        {
            font = _font,
            fontSize = Mathf.Max(8, Mathf.RoundToInt(GlassMenuTitleFontSize * _namedTheme.FontScale)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(GlassMenuTitlePadX, GlassMenuTitlePadX, 0, 0),
        };
        style.normal.textColor = ToUnity(Colors.MenuText);
        return style;
    }

    private GUIStyle BuildGlassMenuCloseStyle()
    {
        var style = new GUIStyle
        {
            font = _font,
            fontSize = Mathf.Max(8, Mathf.RoundToInt(GlassMenuCloseFontSize * _namedTheme.FontScale)),
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(0, 0, 0, 0),
        };
        style.normal.textColor = ToUnity(Colors.MenuMuted);
        return style;
    }

    /// <summary>
    /// Renders GlassMenu chrome inside the window's IMGUI drawer.
    /// Coordinates are window-local (0,0 = top-left). Reserves a title-bar
    /// row in the layout so the plugin body draws below.
    /// </summary>
    private void DrawGlassMenuChrome(string title, WindowRect windowRect, Action? onClose)
    {
        EnsureSettingsResources();
        if (_glassMenuBgStyle is null
            || _glassMenuTitleStyle is null
            || _glassMenuCloseStyle is null
            || _settingsWhiteTex is null
            || _glassMenuBgTex is null) return;

        float w = windowRect.Width;
        float h = windowRect.Height;

        DrawGlassMenuBackground(w, h);
        // Border is baked into the rounded gradient texture (corner-following),
        // so there's no separate rectangular border draw — that produced the
        // double edge (rounded gradient + straight rect frame).
        DrawGlassMenuTitleBar(title, w, hasClose: onClose is not null);
        if (onClose is not null)
        {
            var closeRect = DrawGlassMenuCloseGlyph(w);
            HandleGlassMenuCloseClick(closeRect, onClose);
        }
        RegisterGlassMenuDragRegion(w, hasClose: onClose is not null);

        // 2px header accent-line ties the title to the border (one accent).
        if (_settingsWhiteTex != null)
        {
            var prevA = GUI.color;
            GUI.color = ToUnity(Colors.MenuAccent);
            GUI.DrawTexture(new UnityRect(0f, GlassMenuTitleBarHeight - 2f, w, 2f), _settingsWhiteTex);
            GUI.color = prevA;
        }

        ReserveGlassMenuTitleSpace();
    }

    /// <summary>
    /// Public: draw ONLY the rounded glass panel background (no title bar) for a
    /// window that manages its own header — e.g. the launcher, which puts the
    /// header on a configurable side (top / left). Lets the chrome's glass look
    /// be reused without forcing a top title bar. Returns the header strip
    /// thickness so the caller can inset its body.
    /// </summary>
    public int DrawGlassMenuPanel(WindowRect windowRect)
    {
        EnsureSettingsResources();
        if (_glassMenuBgStyle is null || _glassMenuBgTex is null) return GlassMenuTitleBarHeight;
        DrawGlassMenuBackground(windowRect.Width, windowRect.Height);
        return GlassMenuTitleBarHeight;
    }

    /// <summary>
    /// Public: draw a top header strip's 2px accent line + a drag handle (the
    /// caller draws its own header content + close on top). <paramref name="thickness"/>
    /// is the strip height; <paramref name="dragInsetStart"/>/<paramref name="dragInsetEnd"/>
    /// trim the interactive ends out of the drag region.
    /// </summary>
    public void DrawGlassMenuHeaderStrip(WindowRect windowRect, float thickness, float dragInsetStart, float dragInsetEnd)
    {
        if (_settingsWhiteTex is null) return;
        float w = windowRect.Width;
        var prev = GUI.color;
        GUI.color = ToUnity(Colors.MenuAccent);
        GUI.DrawTexture(new UnityRect(0f, thickness - 2f, w, 2f), _settingsWhiteTex);   // accent at strip's bottom
        GUI.color = prev;

        var drag = new UnityRect(dragInsetStart, 0f, Mathf.Max(0f, w - dragInsetStart - dragInsetEnd), thickness);
        GUI.DragWindow(drag);
    }

    private void DrawGlassMenuBackground(float w, float h)
    {
        // GUI.Box with the 9-sliced style. Border on the style = corner radius
        // so the alpha-cutout corners baked into the gradient survive at any
        // window dimension.
        GUI.Box(new UnityRect(0f, 0f, w, h), GUIContent.none, _glassMenuBgStyle);
    }

    private void DrawGlassMenuTitleBar(string title, float w, bool hasClose)
    {
        float rightInset = hasClose ? GlassMenuCloseHitWidth + 4f : 0f;
        var titleRect = new UnityRect(0f, 0f,
            Mathf.Max(0f, w - rightInset),
            GlassMenuTitleBarHeight);
        FitTitleFontSize(title, titleRect.width);
        DrawCenteredTitle(titleRect, title, _glassMenuTitleStyle!);
    }

    // Auto-shrink the (shared) title style's font so a narrow window's title
    // fits in the space left of the ✕ instead of clipping. Wide windows fit at
    // the base size on the first iteration (no-op); recomputed every draw so the
    // shared style never leaks a shrunk size onto the next, wider window.
    private void FitTitleFontSize(string title, float titleWidth)
        => _glassMenuTitleStyle!.fontSize = ComputeFittedTitleSize(title, titleWidth);

    // The font size the GlassMenu title bar will use for <paramref name="title"/>
    // at this window width. Lets a body drawer (e.g. the launcher's Minimal
    // column) match its own label size to the header so they read consistently.
    public int ResolveGlassMenuTitleFontSize(string title, float windowWidth, bool hasClose)
    {
        if (_glassMenuTitleStyle is null) return GlassMenuTitleFontSize;
        float rightInset = hasClose ? GlassMenuCloseHitWidth + 4f : 0f;
        return ComputeFittedTitleSize(title, Mathf.Max(0f, windowWidth - rightInset));
    }

    // Largest size in [min, base] whose text fits the available width; restores
    // the style's fontSize so callers that only want the number aren't disturbed.
    private int ComputeFittedTitleSize(string title, float titleWidth)
    {
        var style = _glassMenuTitleStyle!;
        int baseSize = Mathf.Max(GlassMenuTitleMinFontSize,
                                 Mathf.RoundToInt(GlassMenuTitleFontSize * _namedTheme.FontScale));
        float avail = titleWidth - style.padding.left - 2f;
        var content = new GUIContent(title);
        int saved = style.fontSize, result = GlassMenuTitleMinFontSize;
        for (int size = baseSize; size > GlassMenuTitleMinFontSize; size--)
        {
            style.fontSize = size;
            float textWidth = style.CalcSize(content).x - style.padding.left - style.padding.right;
            if (textWidth <= avail) { result = size; break; }
        }
        style.fontSize = saved;
        return result;
    }

    private UnityRect DrawGlassMenuCloseGlyph(float w)
    {
        var rect = new UnityRect(w - GlassMenuCloseHitWidth, 0f,
            GlassMenuCloseHitWidth, GlassMenuTitleBarHeight);
        var style = _glassMenuCloseStyle!;
        var prev = GUI.color;
        try
        {
            var ev = Event.current;
            if (ev != null && rect.Contains(ev.mousePosition))
                GUI.color = ToUnity(Colors.MenuAccent);
            GUI.Label(rect, "✕", style);
        }
        finally
        {
            GUI.color = prev;
        }
        return rect;
    }

    private static void HandleGlassMenuCloseClick(UnityRect closeRect, Action? onClose)
    {
        if (onClose is null) return;
        var ev = Event.current;
        if (ev is null || ev.type != EventType.MouseDown || ev.button != 0) return;
        if (!closeRect.Contains(ev.mousePosition)) return;
        onClose();
        ev.Use();
    }

    private static void RegisterGlassMenuDragRegion(float w, bool hasClose)
    {
        float rightInset = hasClose ? GlassMenuCloseHitWidth + 4f : 0f;
        var dragRect = new UnityRect(0f, 0f,
            Mathf.Max(0f, w - rightInset),
            GlassMenuTitleBarHeight);
        GUI.DragWindow(dragRect);
    }

    private static void ReserveGlassMenuTitleSpace()
    {
        GUILayoutUtility.GetRect(0f, GlassMenuTitleBarHeight, GUIStyle.none, GUILayout.ExpandWidth(true));
    }

    /// <summary>
    /// Per-window body text colour for the raw <c>GUILayout</c> widgets (Label,
    /// Button, Toggle, TextField, Box) a plugin draws after its chrome. The
    /// chrome's own title text already themes via cached styles; this targets
    /// the <i>live</i> <c>GUI.skin</c> that bare widget calls inherit.
    /// <para>
    /// GlassMenu uses <c>MenuText</c> — dark on the Light preset's light glass,
    /// light on the dark presets. Every other style uses <c>TextPrimary</c>,
    /// which stays light in all presets so HUD / Borderless / Tracker /
    /// SettingsDialog bodies remain legible over
    /// the transparent-over-world background. Applied on every chrome draw
    /// because <c>GUI.skin</c> is process-global: without the else-branch a
    /// prior GlassMenu draw would leak dark text onto the next HUD body.
    /// </para>
    /// </summary>
    private void ApplyBodyTextColors(WindowPanelStyle style)
    {
        var c = ToUnity(style == WindowPanelStyle.GlassMenu ? Colors.MenuText : Colors.TextPrimary);
        SetBodyTextStates(GUI.skin.label, c);
        // GUI.skin.button text colour is owned by ApplyButtonStyle (per chrome-button
        // style — Filled needs a dark label), so it is intentionally NOT set here.
        SetBodyTextStates(GUI.skin.box, c);
        SetBodyTextStates(GUI.skin.toggle, c);
        SetBodyTextStates(GUI.skin.textField, c);
    }

    private static void SetBodyTextStates(GUIStyle s, Color c)
    {
        s.normal.textColor    = c;
        s.hover.textColor     = c;
        s.active.textColor    = c;
        s.focused.textColor   = c;
        s.onNormal.textColor  = c;
        s.onHover.textColor   = c;
        s.onActive.textColor  = c;
        s.onFocused.textColor = c;
    }
}
