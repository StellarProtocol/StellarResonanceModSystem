using System;
using System.Collections.Generic;

namespace Stellar.Abstractions.Domain;

/// <summary>What a hypertext message (or one of its segments) links to. Explicit values: append new members at the
/// end only (a compiled plugin bakes the numbers in).</summary>
public enum ChatLinkKind
{
    /// <summary>Plain text run (segments only; never a whole message's kind).</summary>
    Text = 0,
    /// <summary>A map position — <see cref="ChatTextSegment.Position"/>; pass it to
    /// <see cref="Services.IChatPositionShare.TrackPosition"/> to do what the game's link click does.</summary>
    Position = 1,
    /// <summary>An item — <see cref="ChatTextSegment.ItemId"/> / <see cref="ChatTextSegment.ItemQuality"/>.</summary>
    Item = 2,
    /// <summary>A player (name in a notice, or a personal-space share) — <see cref="ChatTextSegment.PlayerId"/>.</summary>
    Player = 3,
    /// <summary>A home share — <see cref="ChatTextSegment.PlayerId"/> is the owner.</summary>
    Home = 4,
    /// <summary>A homestead blueprint share.</summary>
    Blueprint = 5,
    /// <summary>Anything else (fishing records, master seals, guild/system notices).</summary>
    Other = 6,
}

/// <summary>
/// The structured content of a hypertext chat message (<c>ChatMsgInfo.chat_hypertext</c>, msg_type Hypertext →
/// <see cref="ChatMessageType.ItemLink"/>): position / item / player / home / blueprint shares and the server's
/// templated notices (dungeon loot rolls, guild events). <see cref="Segments"/> is the message as the game displays
/// it, already split into plain-text and link runs, with names resolved through the game's live (localised) tables.
/// Delivered on <see cref="ChatMessage.Hypertext"/>; init-only so it can grow without a binary break.
/// </summary>
public sealed record ChatHypertext
{
    /// <summary>The ChatHyperlink table row (e.g. 3000002 position share, 3000001 item share, 3001001 personal
    /// space, 6000001 home, 6000002 blueprint, 1043011/1043013 loot rolls).</summary>
    public int ConfigId { get; init; }

    /// <summary>What the message's main link is: the kind of a share, or <see cref="ChatLinkKind.Other"/> for a
    /// templated notice (whose player/item segments still carry their own kinds). Never <see cref="ChatLinkKind.Text"/>.</summary>
    public ChatLinkKind Kind { get; init; } = ChatLinkKind.Other;

    /// <summary>True for a player SHARE (position, item, personal space, home, blueprint, fishing, master seal) —
    /// text the sender typed plus one link; false for a server/system notice built from a template.</summary>
    public bool IsShare { get; init; }

    /// <summary>The display, in order: <see cref="ChatLinkKind.Text"/> runs and link runs. Adjacent text is merged
    /// and empty runs are dropped. Every <see cref="ChatTextSegment.Text"/> is sanitised the way the game sanitises
    /// chat (all rich tags except <c>&lt;sprite=N&gt;</c> removed, line breaks and backslashes dropped). Never null.
    /// Empty only on a message the framework could not lay out (then <see cref="ChatMessage.Text"/> is the fallback).</summary>
    public IReadOnlyList<ChatTextSegment> Segments { get; init; } = Array.Empty<ChatTextSegment>();

    /// <summary>All segment texts joined — the message as one plain string (e.g. for copy, search or a log line).</summary>
    public string PlainText { get; init; } = string.Empty;

    /// <summary>The raw wire slots the segments were built from, in wire order. Never null.</summary>
    public IReadOnlyList<ChatPlaceholder> Placeholders { get; init; } = Array.Empty<ChatPlaceholder>();
}

/// <summary>One display run of a <see cref="ChatHypertext"/>: plain text, or a link with its typed target.</summary>
public sealed record ChatTextSegment
{
    /// <summary><see cref="ChatLinkKind.Text"/> for plain text, otherwise what the link points at.</summary>
    public ChatLinkKind Kind { get; init; }

    /// <summary>Display text (sanitised; see <see cref="ChatHypertext.Segments"/>). Brackets the game draws around a
    /// share link (<c>[ … ]</c>) are in the neighbouring text runs, not in the link's own text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The position a <see cref="ChatLinkKind.Position"/> link points at; null for every other kind.</summary>
    public ChatPosition? Position { get; init; }

    /// <summary>Item config id for an <see cref="ChatLinkKind.Item"/> link (also the fish id of a fishing share); 0 otherwise.</summary>
    public int ItemId { get; init; }

    /// <summary>The item's quality tier (ItemTable <c>Quality</c>, higher = rarer — the game colours the name by it);
    /// 0 when unknown or not an item.</summary>
    public int ItemQuality { get; init; }

    /// <summary>Char id of the player a <see cref="ChatLinkKind.Player"/> link names, or the owner of a
    /// <see cref="ChatLinkKind.Home"/> share; 0 when unknown / not a player link.</summary>
    public long PlayerId { get; init; }
}
