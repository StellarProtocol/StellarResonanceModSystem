using Stellar.Abstractions.Services;
using UnityEngine;
using SysMath = System.Math;

namespace Stellar.Infrastructure.Theme;

// Phase 8 semantic-text helpers for plugins. Sizes mirror HTML heading +
// paragraph levels (mockup theme-palette-v3 / panel-styles base hierarchy);
// each style is pre-built once at Initialise time and reused across draws.
// FontScale = 1.0 in Phase 8 (Settings UI ships in Phase 9).
internal sealed partial class ThemeRenderer : IThemeText
{
    // Base sizes at FontScale=1.0. Phase 9 settings UI multiplies these via
    // FontScale before constructing each cached GUIStyle in
    // BuildTextStyles(); when FontScale changes the styles must be rebuilt
    // (currently constant, no rebuild path wired).
    private const int H1BaseSize      = 20;
    private const int H2BaseSize      = 17;
    private const int H3BaseSize      = 15;
    private const int H4BaseSize      = 14;
    private const int BodyBaseSize    = 13;
    private const int CaptionBaseSize = 11;

    private GUIStyle? _h1Style;
    private GUIStyle? _h2Style;
    private GUIStyle? _h3Style;
    private GUIStyle? _h4Style;
    private GUIStyle? _bodyStyle;
    private GUIStyle? _captionStyle;

    /// <summary>
    /// Phase 9a: returns the live <see cref="INamedTheme.FontScale"/> value
    /// (clamped to [0.8, 1.4] inside the service). When the user changes it
    /// via the Themes panel, <see cref="OnNamedThemeChanged"/> sets
    /// <c>_initialised</c> back to false so the next OnGUI Initialise pass
    /// rebuilds every cached GUIStyle against the new scale.
    /// </summary>
    public float FontScale => _namedTheme.FontScale;

    public IThemeText Text => this;

    /// <summary>
    /// Built once at Initialise (called from BuildGuiStyles in the main partial).
    /// Idempotent — safe to call again after FontScale changes.
    /// </summary>
    internal void BuildTextStyles()
    {
        _h1Style      = MakeTextStyle(H1BaseSize,      FontStyle.Bold,   muted: false);
        _h2Style      = MakeTextStyle(H2BaseSize,      FontStyle.Bold,   muted: false);
        _h3Style      = MakeTextStyle(H3BaseSize,      FontStyle.Bold,   muted: false);
        _h4Style      = MakeTextStyle(H4BaseSize,      FontStyle.Bold,   muted: false);
        _bodyStyle    = MakeTextStyle(BodyBaseSize,    FontStyle.Normal, muted: false);
        _captionStyle = MakeTextStyle(CaptionBaseSize, FontStyle.Normal, muted: true);

        // INTENTIONALLY NO fixedHeight pin on text styles. The DrawCentered
        // helper (BeginVertical + FlexibleSpace + Label + FlexibleSpace) only
        // works if the Label inside has its NATURAL text height — with a
        // fixedHeight pin the Label fills the entire LineBody slot and leaves
        // zero space for the FlexibleSpaces to expand into, defeating the
        // centering. Row height is owned by the outer BeginVertical's
        // GUILayout.Height(LineBody) in DrawCentered, not by the style.
    }

    private GUIStyle MakeTextStyle(int baseSize, FontStyle weight, bool muted)
    {
        // alignment=MiddleLeft + stretchWidth=false: the text sits vertically
        // centred in its fixedHeight slot and the label takes only its natural
        // width (so neighbouring siblings aren't pushed off the row by
        // GUI.skin.label's default stretchWidth=true).
        var style = new GUIStyle(GUI.skin.label)
        {
            font = _font,
            fontSize = Mathf.Max(8, Mathf.RoundToInt(baseSize * FontScale)),
            fontStyle = weight,
            alignment = TextAnchor.MiddleLeft,
            stretchWidth = false,
            margin = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(0, 0, 0, 0),
        };
        style.normal.textColor = muted ? ToUnity(Colors.TextMuted) : ToUnity(Colors.TextPrimary);
        return style;
    }

    // Row line-heights: ceil(base * FontScale / 4) * 4.
    private int LineH1   => ScaledLine(36);
    private int LineH2   => ScaledLine(28);
    private int LineH3   => ScaledLine(24);
    private int LineBody => ScaledLine(20);

    private int ScaledLine(int baseH)
        => (int)SysMath.Ceiling(baseH * _namedTheme.FontScale / 4.0) * 4;

    public void DrawH1(string text)      => DrawCentered(text, _h1Style,      LineH1,   muted: false);
    public void DrawH2(string text)      => DrawCentered(text, _h2Style,      LineH2,   muted: false);
    public void DrawH3(string text)      => DrawCentered(text, _h3Style,      LineH3,   muted: false);
    public void DrawH4(string text)      => DrawCentered(text, _h4Style,      LineBody, muted: false);
    public void DrawBody(string text)    => DrawCentered(text, _bodyStyle,    LineBody, muted: false);
    public void DrawCaption(string text) => DrawCentered(text, _captionStyle, LineBody, muted: true);

    private void DrawCentered(string text, GUIStyle? style, int rowHeight, bool muted)
    {
        if (style is null) return;
        // Give the text a full-row-height rect and let the GUIStyle's
        // TextAnchor.MiddleLeft handle vertical centring inside.
        var content = new GUIContent(text);
        var textSize = style.CalcSize(content);
        var rect = GUILayoutUtility.GetRect(textSize.x, rowHeight, GUILayout.ExpandWidth(false));
        var draw  = InkCentered(rect, textSize);
        GUI.Label(draw, content, style);
    }

    /// <summary>
    /// Unity's <c>TextAnchor.MiddleLeft</c> centres the font's LINE BOX in the
    /// rect, but the visible ink (caps/x-height) sits low within that line box,
    /// so centred text renders a couple px too low. Shift the draw rect up by a
    /// fraction of the measured text height so the GLYPH — not the line box —
    /// centres in the row. Factor verified against the AutoNav pill via the
    /// tools/ui-sandbox pixel-measurement gate (was +2.5px low at body size).
    /// </summary>
    private static UnityEngine.Rect InkCentered(UnityEngine.Rect rect, Vector2 textSize)
    {
        float nudge = Mathf.Round(textSize.y * 0.14f);
        return new UnityEngine.Rect(rect.x, rect.y - nudge, rect.width, rect.height);
    }

}
