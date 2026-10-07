using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game;

/// <summary>What a character is to the local player, as the game's own photo-screen visibility reads it
/// (<c>ZEntityMgr.isCharVisibleBySource</c>, release_3.7): the local player itself, a party member (the local player's
/// team), a friend (<c>LuaDataMgr.IsFriend</c>), a guild mate (<c>LuaDataMgr.IsUnionMember</c>). A player can be several.</summary>
[Flags]
internal enum CharacterRelation
{
    None = 0,
    Local = 1,
    Party = 2,
    Friend = 4,
    Guild = 8,
}

/// <summary>
/// Framework 2.20.0 per-character hides on top of the game photo screen's own switches (owner go 2026-10-07, "go i want
/// all"). Pure — unit-tested; <see cref="GameCharacterHides"/> applies the answers in game.
/// <para><b>Body.</b> The game's Friends / Party / Guild switches only stop RESCUING their members from a hidden "Other
/// adventurers"; on their own they hide nobody (recon photo-hide-recon.md § Q2). The framework adds the missing half:
/// HIDE WINS — a player in ANY hidden group is hidden, even while Other adventurers is shown (owner MAIN 2026-10-08: guild
/// mate "Sey", also a friend, stayed visible with only Guild ticked under the earlier "any shown group rescues" rule).
/// One exception keeps the 2026-10-01 "Keep my party visible hides the party anyway" fix: a party member stays visible
/// while Party itself is shown, whatever else they are. Strangers (no group) are left to Other adventurers; the local
/// player is never hidden here (that is the Me switch).</para>
/// <para><b>Weapon.</b> The game's Weapon switch (<c>SetEntityShow(10)</c>) hides only the LOCAL player's weapon
/// (<c>PlayerEnt</c>); the framework hides every other player's weapon too. The local weapon stays with the game's own
/// switch (held by the entity-show plan), so this rule never answers true for <see cref="CharacterRelation.Local"/>.</para>
/// </summary>
internal static class CharacterHideRules
{
    /// <summary>The layers that need a per-character pass.</summary>
    public const VisibilityLayers Driven =
        VisibilityLayers.Friends | VisibilityLayers.Party | VisibilityLayers.Guild | VisibilityLayers.Weapons;

    /// <summary>The group layers (membership matters only while one of them is requested).</summary>
    public const VisibilityLayers Groups = VisibilityLayers.Friends | VisibilityLayers.Party | VisibilityLayers.Guild;

    /// <summary>True when the request needs any per-character write.</summary>
    public static bool Needed(VisibilityLayers requested) => (requested & Driven) != 0;

    /// <summary>True when the character's body (model + effects) is hidden by the framework's own source.</summary>
    public static bool HideBody(VisibilityLayers requested, CharacterRelation relation)
    {
        if ((relation & CharacterRelation.Local) != 0) return false;
        if ((relation & CharacterRelation.Party) != 0 && (requested & VisibilityLayers.Party) == 0) return false;
        return InHiddenGroup(requested, relation, CharacterRelation.Party, VisibilityLayers.Party)
            || InHiddenGroup(requested, relation, CharacterRelation.Friend, VisibilityLayers.Friends)
            || InHiddenGroup(requested, relation, CharacterRelation.Guild, VisibilityLayers.Guild);
    }

    /// <summary>True when another player's weapon is hidden (the local player's goes through the game's own switch).</summary>
    public static bool HideWeapon(VisibilityLayers requested, CharacterRelation relation) =>
        (requested & VisibilityLayers.Weapons) != 0 && (relation & CharacterRelation.Local) == 0;

    // The character is in this group AND the group's switch is hidden.
    private static bool InHiddenGroup(VisibilityLayers requested, CharacterRelation relation, CharacterRelation group,
        VisibilityLayers layer) =>
        (relation & group) != 0 && (requested & layer) != 0;

    /// <summary>Membership in one of the game's friend / guild caches. They are named "Uuid" sets, but the game's Lua fills
    /// them from char-id lists (union_vm.lua:45, friends_main_vm.lua:281), so ask with both keys. Owner MAIN 2026-10-07:
    /// asking by uuid alone matched nobody — guild mate "Sey" stayed visible with Guild ticked.</summary>
    public static bool InCache(System.Func<long, bool> contains, long uuid, long charId) =>
        contains(uuid) || (charId != 0 && contains(charId));
}
