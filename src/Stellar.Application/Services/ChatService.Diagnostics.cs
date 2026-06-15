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
    /// and how many subscribers see each message. Truncates text to 80
    /// chars for volume control.
    /// </summary>
    private void DiagDispatched(ChatMessage msg, Action<ChatMessage>[]? snapshot)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var dbgText = msg.Text ?? string.Empty;
        if (dbgText.Length > 80) dbgText = dbgText.Substring(0, 80);
        var subCount = snapshot?.Length ?? 0;
        _log.Info($"[Chat] dispatched: channel={msg.Channel} sender='{msg.SenderName}' (id={msg.SenderId}) text='{dbgText}' subscribers={subCount}");
    }
}
