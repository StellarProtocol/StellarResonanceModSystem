using System;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>Game events the freeze reacts to, never a frame watch (owner doctrine: event-driven; perf review).
/// <para><b>Leaving</b> — a <c>ZEntityMgr.RemoveEntity(long uuid, …)</c> prefix (the despawn hook posing already uses; the
/// hooker chains the two callbacks on the one patch): the leaving entity's anim component is dropped from the
/// <c>set_Speed</c> gate (O(1)), so when the game recycles that model for a respawn or summon its writes are not
/// substituted under the dead uuid (review, regression <c>freeze_combat_resume_recycled_comp_*</c>).</para>
/// <para><b>Ride-up</b> — postfixes on <c>Panda.ZGame.VehicleComp.UpdateControllerInfo</c> and <c>UpdatePassengerList</c>
/// (release_3.7 interop: public, parameterless, no managed callers — the vehicle component's own refresh callbacks; the
/// entity is <c>ZComponent.Host</c>): a vehicle whose driver or passengers change while frozen is re-checked once for
/// being the local player's (<see cref="FreezeTargets.ExcludeIfOwnMount"/>) and released if so. Replaces the former
/// 300-frame per-vehicle re-check. Unmeasured in game: personal mounts are part of the player model (run 8a), so this
/// is for multi-seat / public <c>VehicleEnt</c> only. Both return at once unless frozen.</para></summary>
internal sealed partial class GameFreezeBackend
{
    internal const string VehicleCompType = "Panda.ZGame.VehicleComp";

    private Func<object, object?>? _vehicleHost;

    private void InstallLeaveHook(HarmonyGameMethodHooker hooker)
    {
        if (_types.FindType(GameEntityAccess.ManagerType) is not { } mgr) return;   // the appear hook already warned
        try { hooker.PrefixAllOverloads(mgr, "RemoveEntity", OnEntityLeaving); }
        catch (Exception ex) { WarnOnce("leavehook", "entity-leave hook failed: " + ex.Message); }
    }

    private void InstallVehicleHooks(HarmonyGameMethodHooker hooker)
    {
        var comp = _types.FindType(VehicleCompType);
        _vehicleHost = comp is null ? null : FastAccess.Getter<object?>(StellarInterop.FindPropertyUp(comp, "Host"));
        if (comp is null || _vehicleHost is null) { WarnOnce("vehiclehook", "a vehicle you board while frozen may stay frozen (VehicleComp not found)"); return; }
        try
        {
            hooker.PostfixAllOverloads(comp, "UpdateControllerInfo", OnVehicleChanged);
            hooker.PostfixAllOverloads(comp, "UpdatePassengerList", OnVehicleChanged);
        }
        catch (Exception ex) { WarnOnce("vehiclehook", "vehicle hook failed: " + ex.Message); }
    }

    // Prefix on ZEntityMgr.RemoveEntity(long uuid, EDisappearType, bool): args[0] is the uuid; the entity is still alive.
    private void OnEntityLeaving(object? _, object?[] args)
    {
        if (!_speedGate.Armed || args.Length == 0 || args[0] is not long uuid) return;
        _speedGate.Untrack(uuid);
    }

    // Postfix on VehicleComp.UpdateControllerInfo / UpdatePassengerList: the instance is the vehicle's component.
    private void OnVehicleChanged(object? comp, object?[] _)
    {
        if (!_frozen || comp is null) return;
        try
        {
            if (_vehicleHost!(comp) is not { } host || _entities.Live(host) is not { } vehicle) return;
            var uuid = _entities.Uuid(vehicle);
            if (_ledger.Excludes(uuid)) return;
            var own = FreezeTargets.ExcludeIfOwnMount(_entities, _ledger, uuid, FreezeKinds.Vehicle, _entities.VehicleController(vehicle));
            OnVehicleEvent(uuid, own);
            if (own) ReleaseEntity(uuid, "own mount (ride event)");
        }
        catch (Exception ex) { WarnOnce("vehicleevent", "could not re-check a vehicle: " + ex.Message); }
    }
}
