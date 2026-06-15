using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using UnityEngine;
using UnityRect = UnityEngine.Rect;

namespace Stellar.Infrastructure.Theme;

/// <summary>
/// Phase 8 ITheme + framework-chrome implementation. Owns the framework's font
/// + textures + pre-built GUIStyles; renders the Party / Tracker window chrome
/// via IMGUI; provides HP/MP bar helpers + gold pill widget.
/// </summary>
/// <remarks>
/// <para>
/// Phase E: the IMGUI chrome path (<c>IFrameworkChromeRenderer.DrawWindowChrome</c> +
/// its private chrome/draw helpers below) is no longer wired — all Stellar windows are
/// uGUI now. Those helpers are retained as dead-but-compiling code (a later pass can prune
/// them once each is confirmed uncalled). This class survives as <see cref="ITheme"/>: it
/// still owns the live palette, FontScale, and texture baking the uGUI theme assets read.
/// </para>
/// <para>
/// <c>Initialise()</c> (GUIStyle/texture creation deferred to first OnGUI) is likewise
/// legacy IMGUI setup — no longer called now the OnGUI sink is gone. Texture bakers live in
/// the sibling <c>ThemeRenderer.Textures.cs</c> partial.
/// </para>
/// </remarks>
internal sealed partial class ThemeRenderer : ITheme
{
    // Chrome geometry constants — keep in lockstep with the CSS mockup
    // (.superpowers/.../theme-palette-v3.html § "title banner"):
    // banner clip-path: polygon(0 0, calc(100% - 14px) 0, 100% 100%, 0 100%).
    // Chrome heights at 24 so bold 12 px title has ~5 px breathing room each
    // side after vertical centring; previous 18/16 left text edge-to-edge.
    private const int BannerHeight  = 24;
    private const int SlantCapWidth = 14;
    private const int RailWidth     = 3;
    private const int RailGlowWidth = 7;  // soft glow extends ~4px past the rail
    private const int DividerWidth  = 128; // baked gradient texel count; stretched per draw
    private const int TitleRowHeight = 24; // Tracker chrome — height of the dark gradient title row
    private const int TrackerIconSize = 12; // Tracker chrome — mint circle icon (matches CSS width/height)
    private const int TrackerIconGlowSize = 16; // Tracker chrome — soft glow disc drawn behind the icon
    private const int TrackerIconLeftPad = 6;  // gap from window's left edge to the icon
    private const int TrackerIconTitleGap = 6; // gap between the icon and the title text

    private readonly IThemeAssetProvider _assets;
    private readonly IPluginLog _log;
    private readonly INamedTheme _namedTheme;
    private readonly PresetColorsView _colorsView;

    private Font? _font;
    private Texture2D? _accentTex;
    private Texture2D? _bannerBgTex;
    private Texture2D? _slantCapTex;
    private Texture2D? _railGlowTex;
    private Texture2D? _dividerTex;
    private Texture2D? _titleRowBgTex;
    private Texture2D? _trackerDividerTex;
    private Texture2D? _trackerIconTex;
    private Texture2D? _trackerIconGlowTex;
    private GUIStyle?  _bannerStyle;
    private GUIStyle?  _trackerTitleStyle;
    private GUIStyle?  _dividerStyle;
    private bool _initialised;

    public ThemeRenderer(IThemeAssetProvider assets, IPluginLog log, INamedTheme namedTheme,
                         IColorRegistry colorRegistry, IColorResolver colorResolver,
                         IChromeStyle chromeStyle)
    {
        _assets = assets;
        _log = log;
        _namedTheme = namedTheme;
        _chromeStyle = chromeStyle;
        ColorRegistry = colorRegistry;
        _colorsView = new PresetColorsView(_namedTheme, colorResolver);
        Colors = _colorsView;
        // When the user switches preset or font scale, invalidate cached
        // GUIStyles/textures so the next Initialise call rebuilds them.
        _namedTheme.ActiveChanged += OnNamedThemeChanged;
    }

    public IThemeColors Colors { get; }
    public string FontName => _font?.name ?? "(default)";
    public IColorRegistry ColorRegistry { get; }

    private void OnNamedThemeChanged()
    {
        // Drop cached textures BEFORE flipping _initialised so the next
        // BakeTextures call doesn't leak the previously-baked Texture2D
        // instances. Without this destroy pass, switching presets repeatedly
        // (Theme panel) leaks ~15 textures per click — observable as GC
        // memory creep in the Unity profiler.
        //
        // CRITICAL: _font is NOT touched here. The Font Unity object is a
        // process-scope resource loaded once at first Initialise (see LoadFont
        // idempotent guard). If we let LoadFont re-run on every theme/scale
        // change, the previous Font reference would be replaced — and any
        // GUIStyle whose .font is null (i.e. inherits from GUI.skin.font), or
        // any cached skin field still pointing at the OLD font, ends up with
        // a stale/dangling Font handle. The visible symptom: every text glyph
        // in every settings panel disappears when the user moves the FontScale
        // slider, while textures/tickboxes still render. Repro: open Themes,
        // drag the slider — text vanishes. The fix is to keep _font alive
        // across rebuilds; ApplyGuiSkinDefaults below re-asserts GUI.skin.font
        // against the still-valid _font after the style rebuild.
        DestroyBakedTextures();
        _initialised = false;
    }

    /// <summary>
    /// Idempotent first-frame initialisation. Builds the GUIStyles + textures
    /// the renderer needs. MUST be called from inside an OnGUI invocation:
    /// constructing <see cref="GUIStyle"/> instances reads <c>GUI.skin</c>, which
    /// IL2CPP rejects with "You can only call GUI functions from inside OnGUI."
    /// when called from Plugin.Load. The host's OnGUI sink calls this once
    /// before the first <see cref="DrawBanner"/>; subsequent calls are no-ops.
    /// </summary>
    /// <remarks>
    /// Font loading runs only on first call (LoadFont is guarded against
    /// re-entry by a null check on <c>_font</c>). Texture bake + GUIStyle
    /// build + skin-default reapply run on every (re-)init — they are cheap
    /// and necessary so theme/scale changes take effect. The skin-default
    /// reapply at the tail is critical: it re-binds <c>GUI.skin.font</c> to
    /// the still-valid <c>_font</c> in case Unity invalidated the skin's font
    /// fields during the destroy pass.
    /// </remarks>
    public void Initialise()
    {
        if (_initialised) return;
        _initialised = true;

        LoadFont();
        BakeTextures();
        BuildGuiStyles();
        ApplyGuiSkinDefaults();

        // Diagnostic: verify _font survives theme/scale changes. Fires once
        // per Initialise pass (i.e. boot + each FontScale/preset change).
        _log.Info($"[Theme] Initialise: _font={(_font == null ? "null" : _font.name)}, "
                  + $"GUI.skin.font={(GUI.skin.font == null ? "null" : GUI.skin.font.name)}");
        _log.Info($"[Theme] initialised font={FontName}, textures generated");
    }

    /// <summary>
    /// Override Unity's default <c>GUI.skin</c> so plugin code that uses bare
    /// <c>GUILayout.Label(text)</c> / <c>GUILayout.Button(text)</c> (without an
    /// explicit themed style) picks up the framework font, size, weight, and
    /// colour by default. Mockup spec calls for 14-15px bold white text;
    /// Unity's default Arial 11px reads tiny and washes out against bright
    /// terrain. Applied once per process at Initialise time.
    /// </summary>
    private void ApplyGuiSkinDefaults()
    {
        if (_font != null)
        {
            // Set the skin-wide default font as well as each commonly-used
            // style. The skin-wide default is what bare `new GUIStyle()`
            // constructions inherit; without it, ad-hoc styles render in
            // Unity's built-in Arial because their fontless constructor
            // falls back to GUI.skin.font, not to .label.font.
            GUI.skin.font        = _font;
            GUI.skin.label.font  = _font;
            GUI.skin.button.font = _font;
            GUI.skin.box.font    = _font;
            GUI.skin.toggle.font = _font;
            GUI.skin.textField.font = _font;
        }

        // GUI.skin sizes scale with FontScale so raw GUILayout.Button / Toggle /
        // TextField calls track the slider (Themes preset selectors, plugin
        // toggles, slot picker etc.). Clamp ≥ 8 px. Rebuilt on FontScale change.
        int scaled14 = Mathf.Max(8, Mathf.RoundToInt(14 * _namedTheme.FontScale));
        GUI.skin.label.fontSize  = scaled14;
        GUI.skin.label.fontStyle = FontStyle.Bold;
        GUI.skin.label.normal.textColor = ToUnity(Colors.TextPrimary);
        GUI.skin.button.fontSize    = scaled14;
        GUI.skin.box.fontSize       = scaled14;
        GUI.skin.box.normal.textColor = ToUnity(Colors.TextPrimary);
        GUI.skin.toggle.fontSize    = scaled14;
        GUI.skin.textField.fontSize = scaled14;

        // Skin buttons + scrollbar to the game-UI styles (outline button + thumb-only
        // scrollbar). Background/border only; text colour stays chrome-owned.
        ApplyChromeStyles();
    }

    private void BakeTextures()
    {
        _accentTex = MakeTexture(Colors.Accent);
        // Phase 9b: banner gradient follows the theme accent (top = accent
        // lightened ~35% to keep dark text readable; bottom = accent).
        _bannerBgTex = MakeBannerBgTexture(BannerHeight,
            LightenToward(Colors.Accent, 0.35f), Colors.Accent);
        // Banner slant cap: super-sampled (56×72 → 14×18) per-pixel alpha so the
        // diagonal edge antialiases instead of looking pixelated. The boundary
        // condition uses `>=` to close the 1-pixel hairline gap at the join.
        _slantCapTex = MakeSlantCapTextureSupersampled(SlantCapWidth, BannerHeight, Colors.Accent);
        // Rail soft glow: horizontal accent→transparent falloff drawn behind
        // the solid 3px rail (approximates CSS box-shadow 0 0 6px).
        _railGlowTex = MakeRailGlowTexture(RailGlowWidth, Colors.Accent);
        // Divider gradient: 128×1 accent→transparent, stretched horizontally
        // beneath the banner (CSS: accent 0%, .4 at 80%, transparent 100%).
        _dividerTex  = MakeDividerTexture(DividerWidth, Colors.Accent);
        // Tracker title-row background: 128×1 horizontal gradient
        // rgba(20,28,38,.55) → rgba(20,28,38,.15) at 80% → transparent.
        // (panel-styles.html § .pw-tracker .title-row)
        _titleRowBgTex = MakeTitleRowBgTexture(DividerWidth,
            new ColorRgba(20f / 255f, 28f / 255f, 38f / 255f, 0.55f),
            new ColorRgba(20f / 255f, 28f / 255f, 38f / 255f, 0.15f));
        // Tracker divider gradient: 128×1 solid mint 0..30%, fading to
        // transparent at 100% (panel-styles.html § .pw-tracker .grad-divider).
        // Distinct from _dividerTex which fades immediately from 0%.
        _trackerDividerTex = MakeTrackerDividerTexture(DividerWidth, Colors.Accent);
        // Tracker title-row icon: 24×24 mint disc (drawn at 12×12 with bilinear
        // downscaling for smooth AA edges). Mirrors panel-styles.html
        // § .pw-tracker .title-row .left .icon (12px circle, mint, soft glow).
        _trackerIconTex = MakeTrackerIconTexture(TrackerIconSize, Colors.Accent);
        // Soft mint glow disc baked at the larger glow footprint and rendered
        // behind the solid icon to approximate the CSS box-shadow.
        _trackerIconGlowTex = MakeTrackerIconGlowTexture(TrackerIconGlowSize, Colors.Accent);
        BakeGlassMenuTextures();
    }

    private void BuildGuiStyles()
    {
        _bannerStyle = new GUIStyle
        {
            font = _font,
            fontSize = Mathf.Max(8, Mathf.RoundToInt(12 * _namedTheme.FontScale)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(12, 22, 3, 3),
        };
        // Vertical mint gradient (#6efad4 top → #4ad9b8 bottom) for the banner
        // surface; the slant cap is drawn separately on the right.
        _bannerStyle.normal.background = _bannerBgTex;
        _bannerStyle.normal.textColor = new Color(0.04f, 0.10f, 0.08f);

        WireGlassMenuStyles();

        _trackerTitleStyle = BuildTrackerTitleStyle();
        _dividerStyle = BuildDividerStyle();

        // Semantic-size text styles (H1..H4 + Body + Caption) — see
        // ThemeRenderer.Text.cs. Plugins access via _services.Theme.Text.
        BuildTextStyles();
    }

    /// <summary>
    /// Section-divider style: 1 px fixed-height label with the pre-baked
    /// mint gradient as its background. Zero margin — breathing room around
    /// the divider is owned by the call-site, not by the style.
    /// </summary>
    private GUIStyle BuildDividerStyle()
    {
        var style = new GUIStyle
        {
            fixedHeight = 1f,
            stretchWidth = true,
            margin = new RectOffset(0, 0, 0, 0),
        };
        style.normal.background = _dividerTex;
        return style;
    }

    /// <summary>
    /// Tracker title style: white bold title sat over the dark gradient
    /// title-row (panel-styles.html § .pw-tracker .title-row .left:
    /// color:#fff; font-weight:600; font-size:12.5px). Padding is zero
    /// because the title rect is positioned manually in
    /// <see cref="DrawTrackerChrome"/> (offset by the icon footprint).
    /// </summary>
    private GUIStyle BuildTrackerTitleStyle()
    {
        var style = new GUIStyle
        {
            font = _font,
            fontSize = Mathf.Max(8, Mathf.RoundToInt(12 * _namedTheme.FontScale)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(0, 6, 0, 0),
        };
        style.normal.textColor = Color.white;
        return style;
    }

    /// <summary>
    /// Tracker-style chrome: full-width dark-gradient title row at the top of
    /// the window, with a 12px mint circle icon followed by the white bold
    /// title text, followed by a 1px mint gradient divider (solid 0..30%,
    /// then fading to transparent). No rail, no banner. Mirrors
    /// panel-styles.html § .pw-tracker — see <c>.title-row .left</c>
    /// (icon + title, gap:6px, color:#fff) and <c>.title-row .left .icon</c>
    /// (12px mint disc with soft glow).
    /// </summary>
    private void DrawTrackerChrome(string title, WindowRect windowRect)
    {
        var titleRowRect = new UnityRect(0f, 0f, windowRect.Width, TitleRowHeight);
        GUI.DrawTexture(titleRowRect, _titleRowBgTex);

        // Icon (vertically centred in the title row). Soft glow draws first
        // (larger, lower alpha) so the solid icon sits on top of it.
        float iconY = (TitleRowHeight - TrackerIconSize) * 0.5f;
        float glowY = (TitleRowHeight - TrackerIconGlowSize) * 0.5f;
        float glowX = TrackerIconLeftPad + (TrackerIconSize - TrackerIconGlowSize) * 0.5f;
        var glowRect = new UnityRect(glowX, glowY, TrackerIconGlowSize, TrackerIconGlowSize);
        var iconRect = new UnityRect(TrackerIconLeftPad, iconY, TrackerIconSize, TrackerIconSize);
        GUI.DrawTexture(glowRect, _trackerIconGlowTex);
        GUI.DrawTexture(iconRect, _trackerIconTex);

        // Title text offset = icon left-pad + icon width + gap (matches CSS
        // .pw-tracker .title-row .left { gap: 6px }).
        float titleX = TrackerIconLeftPad + TrackerIconSize + TrackerIconTitleGap;
        var titleRect = new UnityRect(titleX, 0f, Mathf.Max(0f, windowRect.Width - titleX), TitleRowHeight);
        DrawCenteredTitle(titleRect, title, _trackerTitleStyle!);

        var dividerRect = new UnityRect(0f, TitleRowHeight, windowRect.Width, 1f);
        GUI.DrawTexture(dividerRect, _trackerDividerTex);

        // Reserve layout space so the plugin's body draws below the chrome.
        // GUIStyle.none to avoid skin-margin leakage.
        GUILayoutUtility.GetRect(0f, TitleRowHeight + 1f, GUIStyle.none, GUILayout.ExpandWidth(true));
    }

    private void DrawLeftRail(float height)
    {
        if (_accentTex is null || _railGlowTex is null) return;
        // Draw the soft glow first (wider, translucent), then the solid 3px
        // rail on top. Both are anchored to the window's left edge (local x=0)
        // and span the full window height (local y=0..height). Coordinates are
        // window-local because GUI.Window translates the IMGUI matrix to the
        // window's top-left before invoking the drawer.
        var glowRect = new UnityRect(0f, 0f, RailGlowWidth, height);
        var railRect = new UnityRect(0f, 0f, RailWidth, height);
        GUI.DrawTexture(glowRect, _railGlowTex);
        GUI.DrawTexture(railRect, _accentTex);
    }

    // Unity MiddleLeft anchors the baseline at rect.center; gentle ~8% upward
    // nudge to align visual glyph centre with rect centre (18% from previous
    // commit overshot — text ended up touching the chrome's top edge).
    private static void DrawCenteredTitle(UnityRect chromeRect, string title, GUIStyle style)
    {
        var content  = new GUIContent(title);
        var textSize = style.CalcSize(content);
        var baselineNudge = style.fontSize * 0.08f;
        var textRect = new UnityRect(chromeRect.x,
            chromeRect.y + (chromeRect.height - textSize.y) / 2f - baselineNudge,
            chromeRect.width, textSize.y);
        GUI.Label(textRect, content, style);
    }

    private static Color ToUnity(ColorRgba c) => new(c.R, c.G, c.B, c.A);

    // Lerp a colour toward white by t (0=unchanged, 1=white); keeps alpha.
    private static ColorRgba LightenToward(ColorRgba c, float t)
        => new(c.R + (1f - c.R) * t, c.G + (1f - c.G) * t, c.B + (1f - c.B) * t, c.A);
}
