using System;

namespace Stellar.Abstractions.Domain;

/// <summary>An incoming chat message delivered to plugins. <c>MsgId</c> is the
/// server-assigned globally-unique id; plugins can use it to deduplicate when
/// the same message arrives via multiple paths (live Notify + history Return).
/// <c>IsHistory</c> is true for messages projected from the
/// <c>GetChipChatRecords</c> reply that the server sends at login — plugins
/// that react to live traffic (e.g. auto-reply, sound notifications) should
/// skip history-replay messages by checking this flag.</summary>
public sealed record ChatMessage(
    ChatChannel          Channel,
    string               SenderName,
    long                 SenderId,
    string               Text,
    DateTime             Timestamp,
    ChatMessageType      Type,
    ReadOnlyMemory<byte> RawPayload,
    SenderMeta?          Sender,
    long                 MsgId,
    bool                 IsHistory = false);
