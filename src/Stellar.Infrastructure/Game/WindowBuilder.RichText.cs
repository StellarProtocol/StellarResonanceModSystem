using System;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

// TextElement.RichEmoji support: inline <sprite=N> chat emoji drawn as images. The game renders chat emoji with
// TextMeshPro (Panda.ZUi.ZTMPText : TextMeshProUGUI) against its own sprite asset, so a RichEmoji body is built
// on the framework's existing TMP styled-text path (TmpStyledText — same shipped Latin+Thai / system CJK faces,
// LayoutElement contract and StyledTextBinding) with RichEmoji set on the spec; TmpEmojiSprites supplies the
// game's sprite asset. (The earlier Panda.ZUi.ZText attempt is gone: ZText is a different <emoji=KEY> system,
// never parses <sprite=N>, and its ctor's isUseRichColorTag_=true left every message BLANK — KB
// Chat-System-Architecture.md §4.) Layout/measurement needs nothing special: TextMeshProUGUI is an
// ILayoutElement, so the bubble width solve (GetPreferredWidth = unwrapped width) and the variable-list row
// measure (GetPreferredHeight = wrapped at the laid-out width) read TMP's own preferred values.
internal sealed partial class WindowBuilder
{
    private static bool _richEmojiFallbackLogged;

    // True = built as TMP. False → BuildText continues to the plain uGUI Text path (literal tags, as before):
    // sandbox / shipped face failed to load (no factory), a CJK string with no CJK face, or any build throw.
    // Defensive by contract — chat text must never break. A throw mid-build may leave a half-made GO under
    // parent; it is destroyed so the fallback Text is the only child laid out.
    private bool TryBuildRichEmojiText(TextElement t, Transform parent, WindowToken token)
    {
        var before = parent.childCount;
        try
        {
            if (TryBuildEmphasisText(t, parent, token)) return true;
            LogRichEmojiFallback("TMP styled-text factory unavailable for this text");
        }
        catch (Exception ex)
        {
            for (var i = parent.childCount - 1; i >= before; i--)
                UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
            LogRichEmojiFallback($"TMP build threw: {ex.Message}");
        }
        return false;
    }

    private void LogRichEmojiFallback(string why)
    {
        if (_richEmojiFallbackLogged) return;
        _richEmojiFallbackLogged = true;
        Warn?.Invoke($"[Window][emoji] RichEmoji text fell back to plain Text (<sprite=N> shows literally): {why}");
    }
}
