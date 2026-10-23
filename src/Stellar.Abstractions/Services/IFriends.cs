using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// The local player's friend list and private-chat (whisper) conversations, read from the game's own data and
/// refreshed every few seconds while in the world; plus the game's own private-chat actions. Pair with
/// <see cref="IChat"/>: whispers arrive there with <see cref="ChatMessage.PeerId"/> = the conversation's
/// <see cref="PrivateChatTarget.CharId"/>, and are sent with <c>ChatTarget.Whisper(charId)</c>.
/// <para>Lists keep their last known contents outside the world (title screen / loading). The actions only
/// queue: they run on the next framework tick in-world, through the same game code the friends window uses, and
/// are dropped silently if the game refuses them. Any thread.</para>
/// </summary>
public interface IFriends
{
    /// <summary>Friends (blocked players excluded), sorted by charId. Replaced as a whole on change.</summary>
    IReadOnlyList<FriendInfo> Friends { get; }

    /// <summary>Private-chat conversations in the game's display order. Replaced as a whole on change.</summary>
    IReadOnlyList<PrivateChatTarget> PrivateChats { get; }

    /// <summary>Fires on the Unity main thread after <see cref="Friends"/> and/or <see cref="PrivateChats"/> changed.</summary>
    event Action Changed;

    /// <summary>Asks the game for the newest page of history with <paramref name="charId"/> (the game only sends
    /// whisper history for a conversation when it is opened). The messages then arrive on
    /// <see cref="IChat.MessageReceived"/> as history (<see cref="ChatMessage.IsHistory"/>) with
    /// <see cref="ChatMessage.PeerId"/> set; ones already delivered are not repeated. Does not mark them read.</summary>
    void RequestPrivateHistory(long charId);

    /// <summary>Marks the conversation with <paramref name="charId"/> read up to its latest message (clears the
    /// game's unread badge too).</summary>
    void MarkPrivateRead(long charId);

    /// <summary>Opens (creates) a conversation with <paramref name="charId"/> in the game's conversation list and
    /// loads its newest history, like starting a chat from a profile card.</summary>
    void CreatePrivateChat(long charId);

    /// <summary>Removes the conversation with <paramref name="charId"/> from the game's conversation list.</summary>
    void ClosePrivateChat(long charId);
}
