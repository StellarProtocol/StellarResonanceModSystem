using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game;

/// <summary>World layers (other players, the player groups, yourself, pets, NPCs, monsters, weapons, collectibles): the
/// game's own photo switches, <c>CameraFrameCtrl.SetEntityShow</c>, refcounted in ZEntityMgr and shared with the game's
/// camera panel. One <see cref="EntityShowPlan"/> holds the union of every requested layer's camera types
/// (<see cref="EntityShowTargets"/>) — each type hidden once and shown once, a type two layers share held once.</summary>
internal sealed partial class GameVisibilityBackend
{
    private readonly EntityShowPlan _entityShow = new();

    private void StepEntities(VisibilityLayers requested)
    {
        var target = EntityShowTargets.For(requested);
        var wrote = _entityShow.NeedsWrite(target, HoldCount);
        var ok = !wrote || Invoke(VisibilityLayers.OtherPlayers, (requested & EntityShowTargets.Layers) != 0, () => SetEntitiesHidden(target));
        _applied = (_applied & ~EntityShowTargets.Layers) | EntityShowTargets.Achieved(requested, _entityShow.Holds);
        if (!wrote) return;
        OnEntitiesSet(target, ok);
        OnEntityHoldsChanged(target);
    }

    /// <summary>Re-assert only: re-writes the held on/off flag types (Me = Oneself 1, Weapon = WeaponsAppearance 10) —
    /// the game's photo-screen exit (<c>ResetCameraInitialParameters</c> writes the local weapon's EPhoto back to its
    /// default, <c>ResetEntityVisible</c> shows Me if the game hid it), a scene change or a re-created local player clears
    /// them without moving any counter (<see cref="EntityShowPlan.RewriteHeldFlags"/>).</summary>
    private void ReassertEntityFlags(VisibilityLayers requested)
    {
        var target = EntityShowTargets.For(requested);
        var any = false;
        foreach (var type in target) any |= EntityShowPlan.IsFlagType(type) && _entityShow.Holds(type);
        if (!any || EntityShowWriter("Entities") is not { } write) return;
        var written = Invoke(VisibilityLayers.SelfCharacter, true, () => _entityShow.RewriteHeldFlags(target, write) > 0);
        OnEntityFlagsReasserted(target, written);
    }

    private bool SetEntitiesHidden(IReadOnlyList<int> target)
    {
        if (EntityShowWriter("Entities") is not { } write) return false;
        return _entityShow.Apply(target, write, HoldCount);
    }

    /// <summary>StellarDiagnostics-only: which flag types a re-assert re-wrote (GameVisibilityBackend.Diagnostics.cs).</summary>
    partial void OnEntityFlagsReasserted(IReadOnlyList<int> target, bool written);

    /// <summary>StellarDiagnostics-only: the entity target and its outcome (GameVisibilityBackend.Diagnostics.cs).</summary>
    partial void OnEntitiesSet(IReadOnlyList<int> target, bool ok);

    /// <summary>StellarDiagnostics-only: logs the live ETakePhotos hold counts after an entity write (GameVisibilityBackend.Diagnostics.cs).</summary>
    partial void OnEntityHoldsChanged(IReadOnlyList<int> target);
}
