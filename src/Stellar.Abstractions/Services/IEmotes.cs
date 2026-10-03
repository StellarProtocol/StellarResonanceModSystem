using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// The local player's unlocked emotes (the same set as the game's emote wheel) and playing one through the game's own
/// emote action — the game's checks and refusal messages apply; no packet is ever built. Main thread only.
/// </summary>
public interface IEmotes
{
    /// <summary>Unlocked emotes, in the game's wheel order. Empty until the player is in the world.</summary>
    IReadOnlyList<EmoteInfo> Unlocked { get; }

    /// <summary>Asks the game to play emote <paramref name="id"/> on the local player.</summary>
    /// <param name="id">An id from <see cref="Unlocked"/>.</param>
    Task<EmoteResult> PlayAsync(int id);

    /// <summary>Raised when <see cref="Unlocked"/> changes (re-read on login and zone changes).</summary>
    event Action? UnlockedChanged;
}
