using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Resolves a player's cosmetic profile data by character/role id through the game's own
/// <c>Social.GetSocialData</c> RPC. v1 exposes a single lookup, <see cref="RequestAvatarUrl"/>,
/// which hands back the player's profile avatar image URL — for chat / nameplate avatars.
/// </summary>
public interface IPlayerProfile
{
    /// <summary>
    /// Resolve the profile avatar image URL for a character/role id (the same id a received chat
    /// message carries as its sender id). Fire-and-forget: the request is issued on the game's Social
    /// proxy and <paramref name="onResult"/> is invoked exactly once on the main (Unity) thread with
    /// the full <c>https://…png</c> URL, or <c>null</c> when the player has no custom photo or the
    /// request failed. Never throws. The service returns only the URL — the caller owns downloading,
    /// caching and turning it into a texture.
    /// </summary>
    /// <param name="charId">The player's character/role id.</param>
    /// <param name="onResult">Main-thread callback receiving the avatar URL, or <c>null</c> on no-photo / failure.</param>
    void RequestAvatarUrl(long charId, Action<string?> onResult);
}
