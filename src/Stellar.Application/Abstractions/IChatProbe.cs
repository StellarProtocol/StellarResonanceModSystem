using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

// Outbound port: Infrastructure adapts the in-game send/receive path. The callback fires on the wire thread.
internal interface IChatProbe
{
    bool TrySend(ChatTarget target, string text, out string? failureReason);
    bool TrySendSticker(ChatTarget target, int stickerConfigId, out string? failureReason);
    Action<ChatMessage>? OnMessageReceived { get; set; }
    // Server reply to a SendChitChatMsg (ours or the game's), correlated to its channel by call_id. Wire thread.
    Action<ChatSendResult>? OnSendResult { get; set; }
}
