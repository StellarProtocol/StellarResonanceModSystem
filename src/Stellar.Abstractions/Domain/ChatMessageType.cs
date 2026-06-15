namespace Stellar.Abstractions.Domain;

/// <summary>Incoming-message classification. Mirrors <c>Zproto.ChitChatMsgType</c>.</summary>
public enum ChatMessageType
{
    /// <summary>Message type not recognised by this version of Stellar.</summary>
    Unknown = 0,
    /// <summary>Normal player-authored text message.</summary>
    Regular,
    /// <summary>Server-generated system notification text.</summary>
    System,
    /// <summary>Role-play / emote action message.</summary>
    Emote,
    /// <summary>Clickable item hyperlink message.</summary>
    ItemLink,
}
