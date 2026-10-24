using System;

namespace Stellar.Abstractions.Domain;

/// <summary>One private-chat (whisper) conversation from the game's own conversation list
/// (<c>IFriends.PrivateChats</c>, in the game's display order: pinned first, then most recent).</summary>
/// <param name="CharId">The other party's charId — matches <see cref="ChatMessage.PeerId"/> on that conversation's
/// whispers.</param>
/// <param name="Name">The other party's name; empty until the game has loaded it.</param>
/// <param name="IsTop">True when the conversation is pinned.</param>
/// <param name="UnreadCount">Unread messages, as the game counts them (latest msg id − last read msg id).</param>
/// <param name="LatestText">Raw text of the latest message (game rich text / <c>&lt;sprite=N&gt;</c> tags kept;
/// empty for a sticker / voice / link message — see <paramref name="LatestMsgType"/>).</param>
/// <param name="LatestMsgType">The latest message's raw <c>ChitChatMsgType</c>: 0 text, 3 sticker, 5 voice,
/// 6 link (hypertext); -1 when the conversation has no message yet.</param>
/// <param name="LatestSenderId">CharId of whoever sent the latest message (the local player or the peer); 0 when none.</param>
/// <param name="LatestTimestampUtc">Server time of the latest message; <c>default</c> when none.</param>
/// <param name="IsOnline">Whether the other party is online, when the game has loaded their profile; null when unknown.</param>
public sealed record PrivateChatTarget(
    long     CharId,
    string   Name,
    bool     IsTop,
    int      UnreadCount,
    string   LatestText,
    int      LatestMsgType,
    long     LatestSenderId,
    DateTime LatestTimestampUtc,
    bool?    IsOnline);
