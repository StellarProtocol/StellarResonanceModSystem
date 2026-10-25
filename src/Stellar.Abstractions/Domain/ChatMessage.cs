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

    /// <summary>The raw <c>ChatMsgInfo.target_id</c> — for a whisper, the RECIPIENT's charId (the same id
    /// <see cref="SenderId"/> uses); 0 when the wire record carried none (non-whisper channels).</summary>
    public long TargetId { get; init; }

    /// <summary>For a <see cref="ChatChannel.Whisper"/> message, the charId of the OTHER party in the
    /// conversation — the key to group whispers per person. Equals <see cref="TargetId"/> when the local player
    /// sent it, otherwise <see cref="SenderId"/>; private-history batches are tagged from the history request's
    /// peer. 0 when it can't be decided (non-whisper, or the local charId isn't known yet and the record carried
    /// a target) — then compare <see cref="SenderId"/>/<see cref="TargetId"/> against
    /// <see cref="Services.IPlayerIdentity.CharId"/> yourself.</summary>
    public long PeerId { get; init; }

    /// <summary>For a client-generated SYSTEM notice (<see cref="ChatChannel.System"/>, e.g. "Obtained Guild Fund×40"),
    /// the notice's source label as the game shows it (e.g. "Obtained"; empty for plain message notices). Null on
    /// every server chat message. These notices never touch the network: the framework captures them from the
    /// game's own chat VM, so they arrive with <see cref="IsHistory"/> = false, no sender, and a framework-made
    /// NEGATIVE <see cref="MsgId"/> (unique per session, not a server id). <see cref="Text"/> keeps the game's
    /// rich-text tags (item-quality <c>&lt;color&gt;</c>) — render it as TMP rich text or strip them.</summary>
    public string? SystemLabel { get; init; }

    /// <summary>System notice kind (the game's <c>E.ESystemTipInfoType</c>: 1 = item gained, 2 = message,
    /// 3 = dungeon roll); 0 on server chat messages. See <see cref="SystemLabel"/>.</summary>
    public int SystemType { get; init; }

    /// <summary>System notice reference id — the item config id for an item-gained notice (<see cref="SystemType"/>
    /// = 1), else the message/config id the game attached (0 when none). See <see cref="SystemLabel"/>.</summary>
    public long SystemRefId { get; init; }
}
