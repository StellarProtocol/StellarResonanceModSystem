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
    bool                 IsHistory = false)
{
    /// <summary>For a picture-emoji/sticker message (<see cref="ChatMessageType.Emote"/>), the sticker's
    /// ChatStickersTable config id (= ChatPictureEmoji.config_id); 0 when the message is not a sticker.
    /// Resolve to an image with <see cref="Services.IGameAssets.LoadStickerIcon(int, out Domain.UvRect)"/>.</summary>
    public int StickerId { get; init; }
}
