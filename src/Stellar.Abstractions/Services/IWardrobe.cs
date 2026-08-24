using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Abstractions.Services;

/// <summary>Save and re-apply the local player's worn cosmetic outfit (fashion). An outfit is a
/// map of <c>FashionRegion</c> code → cosmetic <c>fashionId</c> (<c>0</c> = empty slot); it is
/// applied through the game's own <c>WorldProxy.FashionWear</c> dispatcher, which runs every
/// server-side validation (combat lock, ownership) — plugins never bypass it. Dyes and weapon
/// skins are out of scope in this version.</summary>
public interface IWardrobe
{
    /// <summary>True once the game-side fashion bridge is resolved and the player is in world.</summary>
    bool IsAvailable { get; }

    /// <summary>The local player's currently worn outfit as a region→fashionId map, or
    /// <c>null</c> if it cannot be read yet (bridge unresolved / not in world). Regions the
    /// player has nothing worn in carry <c>0</c>. This is the value to store as a saved slot.</summary>
    /// <returns>The worn region→fashionId map, or <c>null</c> when unavailable.</returns>
    IReadOnlyDictionary<int, int>? GetWornOutfit();

    /// <summary>Apply <paramref name="outfit"/> (region→fashionId; <c>0</c> clears a region)
    /// via the game's <c>FashionWear</c> RPC. The game toasts its own success/error; the result
    /// reflects the RPC outcome. Only one apply may be in flight at a time.</summary>
    /// <param name="outfit">Region→fashionId map to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome of the switch.</returns>
    Task<WardrobeResult> ApplyAsync(IReadOnlyDictionary<int, int> outfit, CancellationToken ct = default);
}

/// <summary>Outcome of <see cref="IWardrobe.ApplyAsync"/>.</summary>
public enum WardrobeResult
{
    /// <summary>The outfit was applied (server returned ok).</summary>
    Success,

    /// <summary>Refused because the player is in combat.</summary>
    InCombat,

    /// <summary>The server rejected the apply (a non-zero game error code; see the log).</summary>
    Rejected,

    /// <summary>The switch did not complete in time.</summary>
    Timeout,

    /// <summary>The switch was cancelled.</summary>
    Cancelled,

    /// <summary>The fashion bridge is not available.</summary>
    GameApiUnavailable,

    /// <summary>The player is not in world.</summary>
    PlayerNotInWorld,
}

/// <summary>The cosmetic wardrobe regions a saved outfit covers (weapon skins excluded).</summary>
public static class WardrobeRegions
{
    /// <summary>The 14 <c>FashionRegion</c> codes an outfit stores, in a stable order:
    /// 701 Suit, 702 UpperClothes, 703 Pants, 711 Gloves, 712 Shoes, 713 Headwear, 714 FaceMask,
    /// 715 MouthMask, 716 Tail, 717 Back, 718 HeadWearSecond, 721 Earrings, 722 Necklace, 723 Ring.
    /// 731 WeaponSkin is deliberately excluded (separate game system).</summary>
    public static IReadOnlyList<int> All { get; } = new[]
    {
        701, 702, 703, 711, 712, 713, 714, 715, 716, 717, 718, 721, 722, 723,
    };
}
