using System;
using System.Collections.Generic;
using System.Text;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// A ChatHyperlink <c>Content</c> template, pre-split into tokens. The game fills these with lua-resty-template
/// (<c>common\placeholder.lua</c>): <c>{*expr*}</c> slots such as <c>{*player.name*}</c> / <c>{*string[1]*}</c> /
/// <c>{*scence.name*}</c> (sic), and TMP tags of which only <c>&lt;link=N&gt;…&lt;/link&gt;</c> matter to us — the
/// link region becomes one link segment. Every other tag is dropped except <c>&lt;sprite=N&gt;</c> (same rule as
/// <see cref="ChatRichText"/>); line breaks and backslashes in literals are dropped too. Immutable once parsed, so
/// instances are cached per template string by <see cref="ChatHypertextBuilder"/>.
/// </summary>
internal sealed class ChatHyperlinkTemplate
{
    internal enum TokenKind { Literal, Slot, LinkOpen, LinkClose }

    internal readonly record struct Token(TokenKind Kind, string Text);

    public IReadOnlyList<Token> Tokens { get; }

    /// <summary>True when the template has a <c>&lt;link&gt;</c> region (shares do; most notices don't).</summary>
    public bool HasLink { get; }

    private ChatHyperlinkTemplate(IReadOnlyList<Token> tokens, bool hasLink) { Tokens = tokens; HasLink = hasLink; }

    public static ChatHyperlinkTemplate Parse(string template)
    {
        var p = new Parser();
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '*')
            {
                int end = template.IndexOf("*}", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    p.Add(TokenKind.Slot, template.Substring(i + 2, end - i - 2).Trim());
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '<' && p.TryTag(template, i, out var close))
            {
                i = close;
                continue;
            }
            if (c is not ('\r' or '\n' or '\\')) p.Literal.Append(c);
        }
        p.Flush();
        return new ChatHyperlinkTemplate(p.Tokens.ToArray(), p.HasLink);
    }

    private sealed class Parser
    {
        public readonly List<Token> Tokens = new();
        public readonly StringBuilder Literal = new();
        public bool HasLink;

        public void Add(TokenKind kind, string text)
        {
            Flush();
            Tokens.Add(new Token(kind, text));
        }

        // A '<'…'>' tag at i: link open/close → token, sprite → literal, anything else → dropped. False = not a tag
        // (no closing '>' or "<>"), so the '<' stays literal text.
        public bool TryTag(string t, int i, out int close)
        {
            close = t.IndexOf('>', i + 1);
            if (close <= i + 1) return false;
            if (string.CompareOrdinal(t, i, "<link", 0, 5) == 0) { Add(TokenKind.LinkOpen, string.Empty); HasLink = true; }
            else if (string.CompareOrdinal(t, i, "</link>", 0, 7) == 0) Add(TokenKind.LinkClose, string.Empty);
            else if (ChatRichText.IsSpriteTag(t, i, close)) Literal.Append(t, i, close - i + 1);
            return true;
        }

        public void Flush()
        {
            if (Literal.Length == 0) return;
            Tokens.Add(new Token(TokenKind.Literal, Literal.ToString()));
            Literal.Clear();
        }
    }
}

/// <summary>
/// Accumulates display segments in order: merges adjacent plain text, appends into an open link run, drops empties.
/// One instance per message build (main thread).
/// </summary>
internal sealed class ChatSegmentList
{
    private readonly List<ChatTextSegment> _done = new();
    private readonly StringBuilder _text = new();
    private ChatTextSegment? _link;          // open link run's data (Text filled at close)
    private readonly StringBuilder _linkText = new();

    public bool InLink => _link is not null;

    public void AddText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_link is not null) _linkText.Append(text);
        else _text.Append(text);
    }

    /// <summary>A standalone link run (a notice's player / item name), outside any template link region.</summary>
    public void AddLink(ChatTextSegment link)
    {
        if (_link is not null) { _linkText.Append(link.Text); return; }   // nested in a region: region wins
        if (link.Text.Length == 0) return;
        FlushText();
        _done.Add(link);
    }

    public void OpenLink(ChatTextSegment data)
    {
        if (_link is not null) return;
        FlushText();
        _link = data;
        _linkText.Clear();
    }

    public void CloseLink()
    {
        if (_link is null) return;
        if (_linkText.Length > 0) _done.Add(_link with { Text = _linkText.ToString() });
        _link = null;
        _linkText.Clear();
    }

    public ChatTextSegment[] ToArray(out string plain)
    {
        CloseLink();     // unbalanced template: close what's open
        FlushText();
        var arr = _done.ToArray();
        var sb = new StringBuilder();
        foreach (var s in arr) sb.Append(s.Text);
        plain = sb.ToString();
        return arr;
    }

    private void FlushText()
    {
        if (_text.Length == 0) return;
        _done.Add(new ChatTextSegment { Kind = ChatLinkKind.Text, Text = _text.ToString() });
        _text.Clear();
    }
}
