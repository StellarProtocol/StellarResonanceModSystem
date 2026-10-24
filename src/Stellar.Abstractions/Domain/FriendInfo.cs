namespace Stellar.Abstractions.Domain;

/// <summary>One entry of the local player's friend list (<c>IFriends.Friends</c>), read from the game's own
/// <c>friend_main_data</c>. Blocked players (the game's Shield group) are excluded.</summary>
/// <param name="CharId">The friend's charId — the same id <see cref="ChatMessage.SenderId"/> carries, so it is
/// directly usable with <c>ChatTarget.Whisper</c> (decoded exactly from the game's int64, not via a float).</param>
/// <param name="Name">Character name; empty until the game has loaded the friend's profile.</param>
/// <param name="Remark">The note the local player set on this friend; empty when none.</param>
/// <param name="Level">Character level; 0 when not loaded.</param>
/// <param name="ProfessionId">Current class (profession) id; 0 when not loaded.</param>
/// <param name="GroupId">Friend group id (the game's custom friend groups; 1 = the default "All" group).</param>
/// <param name="IsOnline">True when the game reports the friend online (offline time 0).</param>
/// <param name="AvatarUrl">Profile picture URL (the same URL <c>IPlayerProfile.RequestAvatarUrl</c> returns), or
/// null when the friend has no custom photo / it isn't loaded.</param>
public sealed record FriendInfo(
    long    CharId,
    string  Name,
    string  Remark,
    int     Level,
    int     ProfessionId,
    int     GroupId,
    bool    IsOnline,
    string? AvatarUrl);
