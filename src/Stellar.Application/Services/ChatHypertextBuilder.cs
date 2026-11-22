using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Turns the raw wire slots of a hypertext chat message into ordered display segments, the way the game's chat VM
/// does (<c>chat_main_vm.lua</c> <c>GetShowMsg</c> → <c>calcNoticeParam</c> / <c>calcHyperLink</c>):
/// <list type="bullet">
/// <item><b>Shares</b> (position, item, personal space, home, blueprint, fishing, master seal): the String slot before
/// the link is <c>string[1]</c>, the one after is <c>string[2]</c>; the template is the ChatHyperlink row of the LINK's
/// kind (e.g. any ScenePosition share renders with row 3000002), its <c>&lt;link&gt;</c> region is the link run.</item>
/// <item><b>Notices</b> (loot rolls, guild events): the message's own row; Val → <c>val</c>/<c>arrVal[i]</c>, Player →
/// <c>player.name</c>/<c>player.names[i]</c> (each a Player link run), Item → <c>item.name</c> (an Item link run),
/// Buff → <c>buff.name</c>, Union → <c>union.build</c>, Timestamp → <c>timestamp</c>.</item>
/// </list>
/// Missing or empty templates fall back to a readable default (<see cref="FallbackFor"/>). Main thread only (lookups
/// touch game tables); parsed templates are cached by template string.
/// </summary>
internal sealed partial class ChatHypertextBuilder
{
    private const int TemplateCacheCap = 256;   // ~75 rows exist; the cap only guards against pathological growth

    private readonly IChatNameLookup _names;
    private readonly Dictionary<string, ChatHyperlinkTemplate> _templates = new(StringComparer.Ordinal);

    public ChatHypertextBuilder(IChatNameLookup names) => _names = names ?? throw new ArgumentNullException(nameof(names));

    /// <summary>Build the delivered <see cref="ChatHypertext"/> from a wire-decoded one (<paramref name="raw"/> carries
    /// only <see cref="ChatHypertext.ConfigId"/> + <see cref="ChatHypertext.Placeholders"/>). <paramref name="sender"/>
    /// fills the fishing/blueprint "player name" slots the game takes from the message sender; <paramref name="msgText"/>
    /// is the template of last resort (guild-group notices put their text there).</summary>
    public ChatHypertext Build(ChatHypertext raw, string sender, string msgText)
    {
        var ctx = new HypertextContext(raw.Placeholders, sender, IsShareRow(raw.ConfigId));
        ctx.Placeholders = NameScenes(raw.Placeholders, ctx);
        var template = ChooseTemplate(raw.ConfigId, ctx, msgText);
        var list = new ChatSegmentList();
        Render(template, ctx, list);
        if (ctx.IsShare && ctx.Link is not null && !template.HasLink) AppendTrailingLink(raw.ConfigId, ctx, list);
        var segments = list.ToArray(out var plain);
        return raw with
        {
            Kind = ctx.IsShare && ctx.Link is not null ? KindOf(ctx.Link.Type) : ChatLinkKind.Other,
            IsShare = ctx.IsShare,
            Segments = segments,
            PlainText = plain,
            Placeholders = ctx.Placeholders,
        };
    }

    // Walk the tokens: literals → text, link region → one link run of the share's kind, slots → resolved pieces.
    private void Render(ChatHyperlinkTemplate template, HypertextContext ctx, ChatSegmentList list)
    {
        foreach (var tok in template.Tokens)
        {
            switch (tok.Kind)
            {
                case ChatHyperlinkTemplate.TokenKind.Literal: list.AddText(tok.Text); break;
                case ChatHyperlinkTemplate.TokenKind.LinkOpen: list.OpenLink(LinkSegment(ctx.Link, ctx)); break;
                case ChatHyperlinkTemplate.TokenKind.LinkClose: list.CloseLink(); break;
                default:
                    var piece = ResolveSlot(tok.Text, ctx);
                    if (piece.Kind == ChatLinkKind.Text) list.AddText(piece.Text);
                    else list.AddLink(piece);
                    break;
            }
        }
    }

    // A share whose template has no <link> (6000001 home: "Come over to my place…" + a "Visit My Home" button): the
    // link becomes a trailing run labelled with the row's button text, else the shared name.
    private void AppendTrailingLink(int configId, HypertextContext ctx, ChatSegmentList list)
    {
        var label = _names.GetHyperlink(configId)?.FunctionButtonEscape;
        if (string.IsNullOrEmpty(label)) label = LinkLabel(ctx.Link!, ctx);
        list.AddText(" [");
        list.OpenLink(LinkSegment(ctx.Link, ctx));
        list.AddText(ChatRichText.Sanitize(label));
        list.CloseLink();
        list.AddText("]");
    }

    private ChatHyperlinkTemplate ChooseTemplate(int configId, HypertextContext ctx, string msgText)
    {
        string? content = null;
        if (ctx.IsShare && ctx.Link is not null && ShareRowFor(ctx.Link.Type) is int linkRow)
            content = _names.GetHyperlink(linkRow)?.Content;
        if (string.IsNullOrEmpty(content)) content = _names.GetHyperlink(configId)?.Content;
        if (string.IsNullOrEmpty(content))
            content = ctx.IsShare && ctx.Link is not null ? FallbackFor(ctx.Link.Type)
                    : msgText.Length > 0 ? msgText
                    : AutoTemplate(ctx);
        return Cached(content!);
    }

    private ChatHyperlinkTemplate Cached(string content)
    {
        if (_templates.TryGetValue(content, out var t)) return t;
        if (_templates.Count >= TemplateCacheCap) _templates.Clear();
        t = ChatHyperlinkTemplate.Parse(content);
        _templates[content] = t;
        return t;
    }

    // Copy of the slots with each position's scene name filled in (so Placeholders and the link segment agree).
    private IReadOnlyList<ChatPlaceholder> NameScenes(IReadOnlyList<ChatPlaceholder> slots, HypertextContext ctx)
    {
        ChatPlaceholder[]? copy = null;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Position is not { } p || p.SceneName is not null) continue;
            var name = _names.GetSceneName(p.SceneId);
            if (name is null) continue;
            copy ??= ToArray(slots);
            copy[i] = slots[i] with { Position = p with { SceneName = ChatRichText.Sanitize(name) } };
            if (ReferenceEquals(ctx.Link, slots[i])) ctx.Link = copy[i];
        }
        return copy ?? slots;
    }

    private static ChatPlaceholder[] ToArray(IReadOnlyList<ChatPlaceholder> slots)
    {
        var a = new ChatPlaceholder[slots.Count];
        for (int i = 0; i < a.Length; i++) a[i] = slots[i];
        return a;
    }
}
