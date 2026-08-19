using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// The TMP styled-text path (i18n typography; owner requirement 2026-08-18: default typography — bold,
// underline, etc. — must work in EVERY language). Both helpers go through the game-only
// StyledTextFactory hook and return "not built" when it is unavailable (Mono sandbox, shipped-font
// load failure, unresolvable CJK face) so callers keep the legacy crisp uGUI Text fallback.
internal sealed partial class WindowBuilder
{
    // Styled TextElements: Emphasis (bold header @15px) and the Bold/Italic/Underline/Strikethrough
    // flags (@ the element's normal size). Weight is a real FACE per script, never synthetic bold.
    private bool TryBuildEmphasisText(TextElement t, Transform parent, WindowToken token)
    {
        var create = StyledTextFactory.CreateBold;
        if (create == null) return false;
        var size = StyledSize(t);
        var h = create(parent, new StyledTextSpec(t.Text(), Scaled(size), _assets.MenuText, wrap: !t.NoWrap)
        {
            Bold = t.Emphasis || t.Bold,
            Italic = t.Italic,
            Underline = t.Underline,
            Strikethrough = t.Strikethrough,
            Align = t.Align,
        });
        if (h?.Go == null) return false;
        // Mirror the legacy path's layout contract: natural width, a sibling Spacer does the pushing.
        var le = h.Go.AddComponent<LayoutElement>();
        if (t.Width > 0f) { le.preferredWidth = le.minWidth = t.Width; le.flexibleWidth = 0f; }
        else { le.flexibleWidth = 0f; le.minWidth = 0f; }
        token.StyledTexts.Add(new StyledTextBinding { H = h, TextFn = t.Text, ColorFn = t.Color });
        token.ReskinActions.Add(() => { h.SetFontSize(Scaled(size)); h.SetColor(_assets.MenuText); });
        return true;
    }

    // Emphasis keeps the header 15px ("same size but bold" — weight, not size, carries the emphasis);
    // plain styled text keeps the body 14px; an explicit FontSize wins over both.
    private static int StyledSize(TextElement t) => t.FontSize > 0 ? t.FontSize : t.Emphasis ? 15 : 14;

    // Window/overlay titles share the same real-bold path (static string → no text binding; the caller
    // registers its own reskin closure when the surface participates in Font Scale). Null → legacy Text.
    private static IStyledTextHandle? TryBuildBoldTitle(Transform parent, string title, int size, Color color)
        => StyledTextFactory.CreateBold?.Invoke(parent, new StyledTextSpec(title, size, color, wrap: false) { Bold = true });

    // Legacy fallback styling for a styled element (sandbox / no TMP face): crisp per-script weight;
    // italic via FontStyle; underline/strikethrough are not renderable on legacy Text and are dropped.
    private static void ApplyLegacyStyledFallback(Text txt, TextElement t)
    {
        var style = UGuiPrimitives.EmphasisStyle(t.Emphasis || t.Bold, t.Text());
        if (t.Italic) style = style == FontStyle.Bold ? FontStyle.BoldAndItalic : FontStyle.Italic;
        txt.fontStyle = style;
    }
}
