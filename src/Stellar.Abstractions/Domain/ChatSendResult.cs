using System;

namespace Stellar.Abstractions.Domain;

/// <summary>
/// The server's answer to one chat send (<c>SendChitChatMsgReply</c>), raised by <c>IChat.SendResult</c> — for sends
/// made through <c>IChat.Send</c>/<c>SendSticker</c> AND sends the player makes in the game's own chat box (see
/// <see cref="ViaFramework"/>). The send cooldown is SERVER-driven (no client config): World/Current reply with a
/// cooldown end (observed 5 s), Party/Guild reply with none.
/// </summary>
/// <param name="Channel">Channel the send went to.</param>
/// <param name="Ok">True when the server accepted the message (<paramref name="ErrorCode"/> = 0).</param>
/// <param name="ErrorCode">The game's <c>EErrorCode</c>; 0 = accepted. <see cref="ErrorTooFrequent"/> = sent before
/// the channel's cooldown ended.</param>
/// <param name="CooldownEndUtc">When the channel may be posted to again (server clock, whole seconds), or null when
/// the reply carried no cooldown.</param>
public readonly record struct ChatSendResult(ChatChannel Channel, bool Ok, int ErrorCode, DateTime? CooldownEndUtc)
{
    /// <summary><c>ErrChatSendCdNoEnd</c> — "Message too frequently, please try again later".</summary>
    public const int ErrorTooFrequent = 5211;

    /// <summary>Framework-made code (never a server one): the send NEVER LEFT THE CLIENT — no live chat connection,
    /// an unsupported target, or the send threw. <see cref="Ok"/> is false and <see cref="ViaFramework"/> true. Raised
    /// before <c>IChat.Send</c>/<c>SendSticker</c> returns when they are called on the main thread (so a chat box can
    /// keep its draft instead of clearing it), otherwise on the main thread right after. No server reply follows.</summary>
    public const int ErrorNotSent = -1;

    /// <summary>True when the send was issued through <c>IChat.Send</c>/<c>SendSticker</c> (by some plugin); false
    /// when the player sent it from the game's own chat box (the game shows its own error tip for those).</summary>
    public bool ViaFramework { get; init; }

    /// <summary>The server time stamped on the accepted message (<c>show_msg.timestamp</c>), or null when the reply
    /// carried none (typically a rejected send).</summary>
    public DateTime? ServerTimeUtc { get; init; }
}
