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
}
