using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Opt-in diagnostics for <see cref="ChatService"/>. Gated behind
/// <c>STELLAR_DIAGNOSTICS=1</c> so the wire→main-thread dispatch path
/// stays quiet under normal play.
///
/// Single entry point called from the main partial file:
/// <see cref="DiagDispatched"/> — fires once per drained message.
/// </summary>
internal sealed partial class ChatService
{
    /// <summary>
    /// Diagnostics mode: log every dispatch across the wire→main-thread
    /// boundary. Confirms whether <see cref="Drain"/> is actually running
    /// and how many subscribers see each message. Ids + lengths only — never
    /// the message text or sender name (chat is private data in a shared log).
    /// </summary>
    private void DiagDispatched(ChatMessage msg, Action<ChatMessage>[]? snapshot)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var subCount = snapshot?.Length ?? 0;
        _log.Info($"[Chat] dispatched: channel={msg.Channel} senderId={msg.SenderId} textLen={msg.Text?.Length ?? 0} subscribers={subCount}");
    }
}
