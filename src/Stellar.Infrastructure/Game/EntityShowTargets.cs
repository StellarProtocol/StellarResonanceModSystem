using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game;

/// <summary>
/// Which <c>CameraFrameCtrl.SetEntityShow</c> camera types (<c>Panda.ZGame.ECamerasysShowEntityType</c>) each world
/// <see cref="VisibilityLayers"/> layer drives. The new 2.20.0 layers are the game photo screen's own switches, one
/// type each, transcribed from its show list (<c>camerasys_data.lua</c> <c>ShowEntityCfg</c>, release_3.7):
/// Self 1, SelfPet 14, Stranger 6, FriendlyNPCS 7, Enemy 9, WeaponsAppearance 10, Chum 2, Team 3, Union 4,
/// Collection 12, OtherPet 15 — its Weapon toggle writes WeaponsAppearance (10), never AngelWeapons (8). The legacy
/// layers keep their meaning: <see cref="VisibilityLayers.OtherPlayers"/> = the master switch 11 (or Stranger + Chum +
/// Union with <see cref="VisibilityLayers.KeepParty"/>), <see cref="VisibilityLayers.Self"/> = Oneself + SelfPet.
/// Overlapping layers share one hold per type (the target is a set). Pure — unit-tested.
/// </summary>
internal static class EntityShowTargets
{
    // Panda.ZGame.ECamerasysShowEntityType — the camera types not already named on EntityShowPlan.
    public const int Team = 3, FriendlyNpcs = 7, Enemy = 9, WeaponsAppearance = 10, Collection = 12, OtherPet = 15;

    private static readonly int[] KeepPartyTypes = { EntityShowPlan.Stranger, EntityShowPlan.Chum, EntityShowPlan.Union };
    private static readonly int[] EveryoneTypes = { EntityShowPlan.OtherPlayer };

    // One row per layer whose types don't depend on another bit; order = the order types are first hidden.
    private static readonly (VisibilityLayers Layer, int[] Types)[] Fixed =
    {
        (VisibilityLayers.Self, EntityShowPlan.SelfSet),
        (VisibilityLayers.SelfCharacter, new[] { EntityShowPlan.Oneself }),
        (VisibilityLayers.OwnSpiritEcho, new[] { EntityShowPlan.SelfPet }),
        (VisibilityLayers.Strangers, new[] { EntityShowPlan.Stranger }),
        (VisibilityLayers.Friends, new[] { EntityShowPlan.Chum }),
        (VisibilityLayers.Party, new[] { Team }),
        (VisibilityLayers.Guild, new[] { EntityShowPlan.Union }),
        (VisibilityLayers.NonPlayers, new[] { FriendlyNpcs }),
        (VisibilityLayers.Enemies, new[] { Enemy }),
        (VisibilityLayers.Weapons, new[] { WeaponsAppearance }),
        (VisibilityLayers.Collectibles, new[] { Collection }),
        (VisibilityLayers.OtherSpiritEchoes, new[] { OtherPet }),
    };

    /// <summary>Every layer this table drives (all go through the one entity-show plan).</summary>
    public const VisibilityLayers Layers = VisibilityLayerSets.World;

    /// <summary>The camera types <paramref name="layer"/> needs held, given the whole request (OtherPlayers reads
    /// KeepParty). Empty for a layer this table doesn't drive.</summary>
    public static int[] TypesOf(VisibilityLayers layer, VisibilityLayers requested)
    {
        if (layer == VisibilityLayers.OtherPlayers)
            return (requested & VisibilityLayers.KeepParty) != 0 ? KeepPartyTypes : EveryoneTypes;
        foreach (var (l, types) in Fixed)
            if (l == layer) return types;
        return Array.Empty<int>();
    }

    /// <summary>The distinct camera types the requested layers need held, in first-hide order (OtherPlayers first,
    /// as before 2.20.0). KeepParty alone (without OtherPlayers) drives nothing.</summary>
    public static IReadOnlyList<int> For(VisibilityLayers requested)
    {
        var target = new List<int>();
        if ((requested & VisibilityLayers.OtherPlayers) != 0) AddDistinct(target, TypesOf(VisibilityLayers.OtherPlayers, requested));
        foreach (var (layer, types) in Fixed)
            if ((requested & layer) != 0) AddDistinct(target, types);
        return target;
    }

    /// <summary>The requested entity layers that are fully in force: every type the layer needs is held. KeepParty is
    /// reported only alongside an achieved OtherPlayers.</summary>
    public static VisibilityLayers Achieved(VisibilityLayers requested, Func<int, bool> held)
    {
        var achieved = VisibilityLayers.None;
        if ((requested & VisibilityLayers.OtherPlayers) != 0 && AllHeld(TypesOf(VisibilityLayers.OtherPlayers, requested), held))
            achieved |= VisibilityLayers.OtherPlayers | (requested & VisibilityLayers.KeepParty);
        foreach (var (layer, types) in Fixed)
            if ((requested & layer) != 0 && AllHeld(types, held)) achieved |= layer;
        return achieved;
    }

    private static bool AllHeld(int[] types, Func<int, bool> held)
    {
        foreach (var t in types)
            if (!held(t)) return false;
        return true;
    }

    private static void AddDistinct(List<int> into, int[] types)
    {
        foreach (var t in types)
            if (!into.Contains(t)) into.Add(t);
    }
}
