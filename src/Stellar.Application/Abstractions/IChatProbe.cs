using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

// Outbound port: Infrastructure adapts the in-game send/receive path. The callback fires on the wire thread.
internal interface IChatProbe
{
    bool TrySend(ChatTarget target, string text, out string? failureReason);
    Action<ChatMessage>? OnMessageReceived { get; set; }
}
