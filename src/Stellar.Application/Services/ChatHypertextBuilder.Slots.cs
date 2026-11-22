using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>Slot resolution for <see cref="ChatHypertextBuilder"/>: the <c>{*expr*}</c> names the game's templates use
/// (<c>chat_main_vm.lua</c> calcNoticeParam / <c>chat_hyperlink\*.lua</c> RefreshShareData), row/kind maps and the
/// fallback templates.</summary>
internal sealed partial class ChatHypertextBuilder
{
    // ChatHyperlink rows the game routes through calcHyperLink (E.ChatHyperLinkType, chat_main_data.lua:10).
    private static readonly HashSet<int> ShareRows = new()
        { 3000001, 3000002, 3001001, 1004020, 1050001, 6000001, 6000002, 8009003, 8009004, 8009005 };

    private static bool IsShareRow(int configId) => ShareRows.Contains(configId);

    // The row the game renders a share with = the row of its LINK's kind (CreateHyperLinkData(type) in calcHyperLink).
    private static int? ShareRowFor(ChatPlaceholderType t) => t switch
    {
        ChatPlaceholderType.Item => 3000001,
        ChatPlaceholderType.ScenePosition => 3000002,
        ChatPlaceholderType.Player => 3001001,
        ChatPlaceholderType.MasterMode => 1050001,
        ChatPlaceholderType.Blueprint => 6000002,
        ChatPlaceholderType.FishItem => 8009003,
        ChatPlaceholderType.FishRank => 8009004,
        ChatPlaceholderType.FishPersonalTotal => 8009005,
        _ => null,   // Homeland: the message's own row (6000001) is used as-is
    };

    private static ChatLinkKind KindOf(ChatPlaceholderType t) => t switch
    {
        ChatPlaceholderType.ScenePosition => ChatLinkKind.Position,
        ChatPlaceholderType.Item => ChatLinkKind.Item,
        ChatPlaceholderType.Player => ChatLinkKind.Player,
        ChatPlaceholderType.Homeland => ChatLinkKind.Home,
        ChatPlaceholderType.Blueprint => ChatLinkKind.Blueprint,
        _ => ChatLinkKind.Other,
    };

    // Used only when the live row is missing or its Content is empty (3000001's is "" in the extracted table).
    private static string FallbackFor(ChatPlaceholderType t) => t switch
    {
        ChatPlaceholderType.ScenePosition =>
            "{*string[1]*}[<link=1>{*scence.name*} · Line {*val1*} ({*val2*}, {*val3*}, {*val4*})</link>]{*string[2]*}",
        ChatPlaceholderType.Item => "{*string[1]*}[<link=1>{*item.name*}</link>]{*string[2]*}",
        _ => "{*string[1]*}[<link=1>{*link.name*}</link>]{*string[2]*}",
    };

    // A notice with no usable row and no msg_text: every slot in wire order, space-separated.
    private static string AutoTemplate(HypertextContext ctx)
    {
        var parts = new List<string>(ctx.Placeholders.Count);
        int p = 0, it = 0, v = 0, b = 0, s = 0;
        foreach (var ph in ctx.Placeholders)
        {
            string? slot = ph.Type switch
            {
                ChatPlaceholderType.Player => $"player.names[{++p}]",
                ChatPlaceholderType.Item => $"item.names[{++it}]",
                ChatPlaceholderType.Val => $"arrVal[{++v}]",
                ChatPlaceholderType.Buff => $"buff.names[{++b}]",
                ChatPlaceholderType.String => $"str[{++s}]",
                ChatPlaceholderType.Union => "union.build",
                ChatPlaceholderType.Timestamp => "timestamp",
                _ => null,
            };
            if (slot is not null) parts.Add("{*" + slot + "*}");
        }
        return string.Join(" ", parts);
    }

    // {*expr*} → a text run, or (notice player/item names) a link run. Unknown names render as "" like a nil in Lua.
    private ChatTextSegment ResolveSlot(string expr, HypertextContext ctx)
    {
        int idx = ParseIndex(expr, out var name);
        return name switch
        {
            "string" => Text(idx == 1 ? ctx.String1 : idx == 2 ? ctx.String2 : null),
            "str" => Text(At(ctx.Strings, idx)?.Text),
            "val" => Text(ctx.Vals.Count == 1 ? Num(ctx.Vals[0]) : null),
            "arrVal" => Text(idx > 0 && idx <= ctx.Vals.Count ? Num(ctx.Vals[idx - 1]) : null),
            "val1" or "val2" or "val3" or "val4" or "val5" => Text(PositionVal(ctx, name[3] - '0')),
            "scence.name" or "scene.name" => Text(SceneLabel(ctx.Link?.Position)),
            "player.name" => ctx.Link is { Type: ChatPlaceholderType.Player } l ? PlayerPiece(l) : PlayerPiece(At(ctx.Players, 1)),
            "player.names" => idx > 0 ? PlayerPiece(At(ctx.Players, idx)) : Text(PlayerNames(ctx)),
            "item.name" => ItemPiece(ctx.Link is { Type: ChatPlaceholderType.Item } l ? l : At(ctx.Items, 1)),
            "item.names" => ItemPiece(At(ctx.Items, idx)),
            "buff.name" or "buff.names" => Text(BuffName(At(ctx.Buffs, Math.Max(idx, 1)))),
            "union.build" => Text(ctx.Union is { } u ? _names.GetUnionBuildingName((int)u.Value) : null),
            "timestamp" => Text(ctx.Timestamp is { } t ? FormatTime(t.Value) : null),
            "fishing.playerName" or "home.playerName" => Text(OwnerName(ctx)),
            "fishing.fishName" => Text(ctx.Link is { } f ? ItemName((int)f.Value) : null),
            "fishing.rank" => Text(ctx.Link is { } r ? Num(r.Rank) : null),
            "link.name" => Text(ctx.Link is { } k ? LinkLabel(k, ctx) : null),
            _ => Text(null),
        };
    }

    // "player.names[2]" → name "player.names", 2. No index → 0.
    private static int ParseIndex(string expr, out string name)
    {
        int open = expr.IndexOf('[');
        name = open < 0 ? expr : expr.Substring(0, open).Trim();
        if (open < 0) return 0;
        int close = expr.IndexOf(']', open + 1);
        return close > open && int.TryParse(expr.AsSpan(open + 1, close - open - 1), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var i) ? i : 0;
    }

    private ChatTextSegment LinkSegment(ChatPlaceholder? link, HypertextContext ctx)
    {
        if (link is null) return new ChatTextSegment { Kind = ChatLinkKind.Other };   // client-side notice link ("Join")
        var seg = new ChatTextSegment { Kind = KindOf(link.Type), Position = link.Position };
        switch (link.Type)
        {
            case ChatPlaceholderType.Item or ChatPlaceholderType.FishItem or ChatPlaceholderType.FishRank:
                _names.TryGetItem((int)link.Value, out _, out var q);
                return seg with { ItemId = (int)link.Value, ItemQuality = q };
            case ChatPlaceholderType.Player or ChatPlaceholderType.Homeland:
                return seg with { PlayerId = link.CharId };
            default:
                return seg;
        }
    }

    // What a share's link is called when the template doesn't say (trailing home button / fallback templates).
    private string LinkLabel(ChatPlaceholder link, HypertextContext ctx) => link.Type switch
    {
        ChatPlaceholderType.Item or ChatPlaceholderType.FishItem or ChatPlaceholderType.FishRank => ItemName((int)link.Value),
        ChatPlaceholderType.ScenePosition => SceneLabel(link.Position),
        _ => OwnerName(ctx),
    };

    private ChatTextSegment PlayerPiece(ChatPlaceholder? p) => p is null ? Text(null)
        : new ChatTextSegment { Kind = ChatLinkKind.Player, Text = ChatRichText.Sanitize(p.Text), PlayerId = p.CharId };

    private ChatTextSegment ItemPiece(ChatPlaceholder? p)
    {
        if (p is null) return Text(null);
        var id = (int)p.Value;
        var name = _names.TryGetItem(id, out var n, out var q) && n.Length > 0 ? n : "#" + Num(id);
        return new ChatTextSegment { Kind = ChatLinkKind.Item, Text = ChatRichText.Sanitize(name), ItemId = id, ItemQuality = q };
    }

    private string ItemName(int id) => _names.TryGetItem(id, out var n, out _) && n.Length > 0 ? n : "#" + Num(id);

    private string? BuffName(ChatPlaceholder? b) => b is null ? null : _names.GetBuffName((int)b.Value);

    // The game's share "player name": the slot's own name (personal space / master / blueprint / angler), else the sender.
    private static string OwnerName(HypertextContext ctx)
        => ctx.Link is { Text.Length: > 0 } l ? l.Text : ctx.Sender;

    private static string PlayerNames(HypertextContext ctx)
    {
        if (ctx.Link is { Type: ChatPlaceholderType.Player or ChatPlaceholderType.MasterMode }) return OwnerName(ctx);
        var names = new string[ctx.Players.Count];
        for (int i = 0; i < names.Length; i++) names[i] = ctx.Players[i].Text;
        return names.Length > 0 ? string.Join(", ", names) : ctx.Sender;
    }

    private static string? PositionVal(HypertextContext ctx, int n)
    {
        if (ctx.Link?.Position is not { } p) return n <= ctx.Vals.Count ? Num(ctx.Vals[n - 1]) : null;
        return Num(n switch { 1 => p.LineId, 2 => p.X, 3 => p.Y, 4 => p.Z, _ => p.MapLayerId });
    }

    private static string SceneLabel(ChatPosition? p)
        => p is null ? string.Empty : p.SceneName ?? "Scene " + Num(p.SceneId);

    // The game formats Timestamp slots with TicksFormatTime(ts*1000, YMDHMS); we use the viewer's local clock.
    private static string? FormatTime(long unixSeconds)
    {
        if (unixSeconds <= 0 || unixSeconds > 253_402_300_799L) return null;
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static ChatTextSegment Text(string? s)
        => new() { Kind = ChatLinkKind.Text, Text = ChatRichText.Sanitize(s) };

    private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static ChatPlaceholder? At(List<ChatPlaceholder> list, int oneBased)
        => oneBased > 0 && oneBased <= list.Count ? list[oneBased - 1] : null;
}
