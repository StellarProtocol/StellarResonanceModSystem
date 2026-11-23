using System.Text;

namespace Stellar.Application.Services;

/// <summary>
/// The game's receive-side chat sanitiser, ported: <c>RichTextHelper.RemoveTagsOtherThanEmojis</c>
/// (<c>common\rich_text_helper.lua:249</c>) — first drop every <c>\r</c>, <c>\n</c> and backslash, then remove every
/// <c>&lt;…&gt;</c> tag except <c>&lt;sprite=N&gt;</c>. Without it a sender could <c>&lt;size=300&gt;</c> /
/// <c>&lt;alpha=#00&gt;</c> every viewer (<c>Chat-System-Architecture.md</c> §8). Pure, any thread.
/// </summary>
internal static class ChatRichText
{
    private static readonly char[] Breaks = { '\r', '\n', '\\' };

    /// <summary>Sanitised copy of <paramref name="text"/>; the SAME instance when nothing needs removing (no alloc).</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        // Same order as the game: the break strip runs first, so "<sprite=1\n2>" becomes a kept "<sprite=12>".
        if (text.IndexOfAny(Breaks) >= 0) text = RemoveBreaks(text);
        return text.IndexOf('<') < 0 ? text : RemoveTags(text);
    }

    private static string RemoveBreaks(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (c is not ('\r' or '\n' or '\\')) sb.Append(c);
        return sb.ToString();
    }

    // Lua pattern "(<[^>]+>)": a '<', at least one char, then the FIRST '>'. No closing '>' (or "<>") = plain text.
    private static string RemoveTags(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            int close = text[i] == '<' ? text.IndexOf('>', i + 1) : -1;
            if (close > i + 1)
            {
                if (IsSpriteTag(text, i, close)) sb.Append(text, i, close - i + 1);
                i = close;
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    /// <summary>True for <c>&lt;sprite=DIGITS&gt;</c> spanning [<paramref name="open"/>, <paramref name="close"/>].</summary>
    internal static bool IsSpriteTag(string s, int open, int close)
    {
        const string head = "<sprite=";
        if (close - open < head.Length + 1 || string.CompareOrdinal(s, open, head, 0, head.Length) != 0) return false;
        for (int k = open + head.Length; k < close; k++)
            if (s[k] is < '0' or > '9') return false;
        return true;
    }
}
