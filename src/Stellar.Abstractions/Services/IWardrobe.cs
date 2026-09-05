using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Abstractions.Services;

/// <summary>Save and re-apply the local player's worn cosmetic outfit (fashion). An outfit is a
/// map of <c>FashionRegion</c> code → cosmetic <c>fashionId</c> (<c>0</c> = empty slot); it is
/// applied through the game's own <c>WorldProxy.FashionWear</c> dispatcher, which runs every
/// server-side validation (combat lock, ownership) — plugins never bypass it. Dyes travel with the
/// pieces server-side (one dye per fashionId). The weapon skin is a SEPARATE per-class game system
/// (the Wardrobe's Weapon Skin tab, class dropdown) and is exposed alongside the outfit through
/// <see cref="GetWornWeaponSkin"/> / <see cref="ApplyWeaponSkinAsync"/>.</summary>
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

    /// <summary>The weapon skin the local player's CURRENT class is wearing, or <c>null</c> if it cannot
    /// be read yet (bridge unresolved / not in world). Weapon skins are per class in the game, so the
    /// value carries the class it belongs to; <c>SkinId</c> <c>0</c> means the class wears its weapon's
    /// own default look ("no skin"). Refreshed together with <see cref="GetWornOutfit"/>.
    /// <para>The game itself has no "skin 0": picking the Wardrobe's ⊘ (no skin) tile stores the id of the
    /// current weapon's ORIGIN row, a concrete skin whose look happens to be the weapon's native one — and
    /// a class has one such row per weapon family. This method reports that case as <c>0</c>, so an outfit
    /// saved with no weapon skin means "no skin" and re-applies as the player's CURRENT weapon's own look
    /// even after they change weapons, rather than pinning them to the weapon they saved with.</para></summary>
    /// <returns>The worn (class, skin) pair, or <c>null</c> when unavailable.</returns>
    WardrobeWeaponSkin? GetWornWeaponSkin();

    /// <summary>Set class <paramref name="professionId"/>'s weapon skin to <paramref name="skinId"/> via
    /// the game's own <c>UseProfessionSkin</c> RPC — the same action the Wardrobe's Weapon Skin tab
    /// performs, so the server validates ownership. <c>skinId</c> <c>0</c> restores the class's default
    /// weapon look, as in the game. Shares the one-apply-at-a-time rule with <see cref="ApplyAsync"/>:
    /// await the outfit switch before sending the weapon skin.</summary>
    /// <param name="professionId">The class whose weapon skin to set.</param>
    /// <param name="skinId">The weapon-skin id, or <c>0</c> for the default look.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome of the switch.</returns>
    Task<WardrobeResult> ApplyWeaponSkinAsync(int professionId, int skinId, CancellationToken ct = default);
}

/// <summary>A worn weapon skin: the class (<c>professionId</c>) it is set for and the skin id
/// (<c>0</c> = the class's default weapon look). Weapon skins are per class in the game and live
/// outside the outfit's <c>FashionRegion</c> map.</summary>
/// <param name="ProfessionId">The class the skin is set for.</param>
/// <param name="SkinId">The weapon-skin id; <c>0</c> = default look.</param>
public sealed record WardrobeWeaponSkin(int ProfessionId, int SkinId);

/// <summary>Outcome of <see cref="IWardrobe.ApplyAsync"/> / <see cref="IWardrobe.ApplyWeaponSkinAsync"/>.</summary>
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

    /// <summary>Preview-only key for the weapon skin (<c>FashionRegion.WeapoonSkin</c> = 731).
    /// Deliberately NOT in <see cref="All"/>: a weapon skin never travels in <c>FashionWear</c>, so it is
    /// not part of a saved outfit's region map and <see cref="IWardrobe.ApplyAsync"/> IGNORES this key —
    /// weapon skins are applied through <see cref="IWardrobe.ApplyWeaponSkinAsync"/>.
    /// <para><see cref="IWardrobePreview.Show"/> alone honours it, dressing the model's weapon with the skin
    /// carried under it. A non-zero value is that skin id; <c>0</c> means "the class's default look" and
    /// resolves the same way applying it would; omitting the key leaves the weapon exactly as the model
    /// renders it.</para>
    /// <para>Weapon skins are per-class (every <c>WeaponSkinTable</c> row carries a <c>ProfessionId</c>), so
    /// only pass a skin belonging to the player's CURRENT class — the game's own weapon-skin tab refuses to
    /// preview another class's skins for the same reason.</para></summary>
    public const int WeaponSkinPreview = 731;
}
