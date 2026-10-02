using System;
using System.Collections.Generic;
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
/// entity is <c>ZComponent.Host</c>): a vehicle whose driver or passengers change while frozen only has its uuid QUEUED
/// here (review, Important finding: <see cref="FreezeTargets.ExcludeIfOwnMount"/> and the release must never run inside
/// the game's own dispatch, and a same-batch <c>AttrRideUuid</c> write may not have landed at the postfix yet). The next
/// <c>LateTick</c> — one frame later, off the game's call stack — re-checks each queued uuid against the CURRENT link
/// and releases it if it is now the local player's. Replaces the former 300-frame per-vehicle re-check. Unmeasured in
/// game: personal mounts are part of the player model (run 8a), so this is for multi-seat / public <c>VehicleEnt</c>
/// only. Both postfixes return at once unless frozen.</para></summary>
internal sealed partial class GameFreezeBackend
{
    internal const string VehicleCompType = "Panda.ZGame.VehicleComp";

    private Func<object, object?>? _vehicleHost;
    private readonly HashSet<long> _pendingVehicleChecks = new();

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
    // Runs only when the removal really happens: a deferred one (.Removal.cs) skips it until its replay. The model's ECS
    // layers are written back BEFORE it is untracked (review 2026-10-02: a pooled model reused with its layers at 0 — the
    // game's own set_Speed skips an unchanged speed); its ECS uid may then be recycled: never gated or replayed again.
    private void OnEntityLeaving(object? _, object?[] args)
    {
        if (!_speedGate.Armed || args.Length == 0 || args[0] is not long uuid) return;
        _speedGate.Untrack(uuid);
        ReleaseEcs(uuid);
    }

    // Postfix on VehicleComp.UpdateControllerInfo / UpdatePassengerList: the instance is the vehicle's component. Only
    // queues the uuid (repeats coalesce in the set) and arms the late driver — the actual re-check runs one LateTick
    // later, outside this callback (review finding).
    private void OnVehicleChanged(object? comp, object?[] _)
    {
        if (!_frozen || comp is null) return;
        try
        {
            if (_vehicleHost!(comp) is not { } host || _entities.Live(host) is not { } vehicle) return;
            _pendingVehicleChecks.Add(_entities.Uuid(vehicle));
            SyncLate();
        }
        catch (Exception ex) { WarnOnce("vehicleevent", "could not queue a vehicle re-check: " + ex.Message); }
    }

    /// <summary>The queued vehicles' re-check, one <c>LateTick</c> after the event — never inside it (review finding): by
    /// now a same-batch <c>AttrRideUuid</c> write has landed, so this reads the CURRENT link. Each uuid (repeats already
    /// coalesced by the set) is isolated in its own try so one failing vehicle never skips the rest, and the queue is
    /// always cleared so nothing is re-checked twice.</summary>
    private void ProcessPendingVehicleChecks()
    {
        foreach (var uuid in _pendingVehicleChecks)
        {
            try
            {
                if (_ledger.Excludes(uuid)) continue;
                var own = FreezeTargets.ExcludeIfOwnMount(_entities, _ledger, uuid, FreezeKinds.Vehicle, _entities.VehicleController(uuid));
                OnVehicleEvent(uuid, own);
                if (own) ReleaseEntity(uuid, "own mount (ride event)");
            }
            catch (Exception ex) { WarnOnce("vehicletick", "could not re-check a queued vehicle: " + ex.Message); }
        }
        _pendingVehicleChecks.Clear();
    }
}
