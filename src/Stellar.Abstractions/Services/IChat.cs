using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Plugin-facing chat service. <c>MessageReceived</c> fires on the main (Unity) thread;
/// <c>Send</c> is fire-and-forget — failures are logged, never thrown.
/// </summary>
public interface IChat
{
    /// <summary>Most recent messages received since the last login (capped by framework; exact cap may vary).</summary>
    IReadOnlyList<ChatMessage> RecentMessages { get; }
    /// <summary>Fires on the Unity main thread each time a new chat message is received.</summary>
    event Action<ChatMessage>  MessageReceived;
    /// <summary>Sends <paramref name="text"/> to the specified <paramref name="target"/> channel. Fire-and-forget; failures are logged, never thrown.</summary>
    void Send(ChatTarget target, string text);
    /// <summary>Sends the picture-emoji / sticker identified by <paramref name="stickerConfigId"/> (a
    /// <c>ChatStickersTable</c> config_id — see <see cref="IGameAssets.GetSendableStickers"/>) to
    /// <paramref name="target"/>. Fire-and-forget; failures are logged, never thrown.</summary>
    void SendSticker(ChatTarget target, int stickerConfigId);

    /// <summary>Remaining SERVER-driven send cooldown on <paramref name="channel"/>, from the last send reply's
    /// cooldown end (sends through this service or the game's own chat box) measured against the framework's
    /// interpolated server clock. Returns false (0 s) when the channel is free or no cooldown has been seen yet —
    /// the server is still the authority, so a send can still be refused (watch <see cref="SendResult"/> for
    /// <see cref="ChatSendResult.ErrorTooFrequent"/>). Any thread.</summary>
    /// <param name="channel">Channel to query (Say = the game's "Current" channel).</param>
    /// <param name="remainingSeconds">Seconds until the channel accepts a new message; 0 when free.</param>
    bool TryGetSendCooldown(ChatChannel channel, out double remainingSeconds)
    {
        remainingSeconds = 0;
        return false;
    }

    /// <summary>Fires on the Unity main thread with the server's reply to each chat send — accepted or refused
    /// (e.g. <see cref="ChatSendResult.ErrorTooFrequent"/>), with the channel's new cooldown end.</summary>
    event Action<ChatSendResult> SendResult
    {
        add { }
        remove { }
    }
}
