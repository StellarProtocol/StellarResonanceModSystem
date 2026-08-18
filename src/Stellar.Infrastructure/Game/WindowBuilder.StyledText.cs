using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// The TMP real-bold text path (i18n P0 JA/TH bold headers; owner requirement 2026-08-18: default
// typography — bold etc. — must work in EVERY language). Both helpers go through the game-only
// StyledTextFactory hook and return "not built" when it is unavailable (Mono sandbox, shipped-font
// load failure, unresolvable CJK face) so callers keep the legacy crisp uGUI Text fallback.
internal sealed partial class WindowBuilder
{
    // Emphasis (section headers): real bold weight in every script at the same 15px the legacy path
    // used ("same size but bold" — weight, not size, carries the emphasis).
    private bool TryBuildEmphasisText(TextElement t, Transform parent, WindowToken token)
    {
        var create = StyledTextFactory.CreateBold;
        if (create == null) return false;
        var h = create(parent, new StyledTextSpec(t.Text(), Scaled(15), _assets.MenuText, wrap: !t.NoWrap));
        if (h?.Go == null) return false;
        // Mirror the legacy path's layout contract: natural width, a sibling Spacer does the pushing.
        var le = h.Go.AddComponent<LayoutElement>();
        if (t.Width > 0f) { le.preferredWidth = le.minWidth = t.Width; le.flexibleWidth = 0f; }
        else { le.flexibleWidth = 0f; le.minWidth = 0f; }
        token.StyledTexts.Add(new StyledTextBinding { H = h, TextFn = t.Text, ColorFn = t.Color });
        token.ReskinActions.Add(() => { h.SetFontSize(Scaled(15)); h.SetColor(_assets.MenuText); });
        return true;
    }

    // Window/overlay titles share the same real-bold path (static string → no text binding; the caller
    // registers its own reskin closure when the surface participates in Font Scale). Null → legacy Text.
    private static IStyledTextHandle? TryBuildBoldTitle(Transform parent, string title, int size, Color color)
        => StyledTextFactory.CreateBold?.Invoke(parent, new StyledTextSpec(title, size, color, wrap: false));
}
