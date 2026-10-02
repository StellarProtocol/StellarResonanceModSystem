using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>Entities that appear while frozen (recon run 3 R3-10). A postfix on <c>ZEntityMgr.AddEntity</c> — main thread,
/// model already live — applies stage 1 and, while the hold is on, holds the entity. For the next
/// <see cref="AppearWatchFrames"/> late frames its anim component is tracked for the <c>set_Speed</c> gate and its drawn
/// speed re-checked; while above 0 the attr write is re-applied (the game resets some players' factor right after
/// <c>AddEntity</c>) and 0 is written again (a re-apply is never a no-op — regression <c>freeze_combat_resume_reapply</c>).
/// The local player's own mount is never frozen (review I1): refused as it appears (<see cref="FreezeTargets.AdmitAppeared"/>,
/// one read); a ride-up that links it later is the game's vehicle event (<c>.Events.cs</c>), never a frame watch.
/// <b>Pooled models</b> (review, regression <c>freeze_combat_resume_recycled_comp_*</c>): the new entity's anim component
/// may be a recycled one the gate still maps to a dead entity, so the postfix RE-KEYS it at once — tracked under the new
/// uuid when admitted, forgotten when refused (the local player, their mount). Event-driven: the hook
/// returns at once unless frozen. <c>onAddEntity</c> and <c>OnModelLoadFinish</c> never fired in the probe and are not
/// hooked. <see cref="TickOneAppeared"/> isolates each watched entity's re-check in its own try so one failure drops
/// only that entity from the watch, never the rest (review finding, Task 9 round 1). <see cref="OnEntityAdded"/>
/// passes the hook's own <c>args[0]</c> entity into stage 1 and the hold instead of re-finding it via
/// <c>GetEntity(uuid)</c> — recon proves the model is live at the postfix, not that the manager's dictionary
/// already serves the uuid back at that same instant (review finding, Task 9 round 2).</summary>
internal sealed partial class GameFreezeBackend
{
    private const int AppearWatchFrames = 30;

    private readonly List<(long Uuid, int Kind, int Frames)> _appeared = new();

    private void InstallAppearHook(HarmonyGameMethodHooker hooker)
    {
        var mgr = _types.FindType(GameEntityAccess.ManagerType);
        if (mgr is null) { WarnOnce("appearhook", "entities appearing while frozen will not freeze (ZEntityMgr not found)"); return; }
        try { hooker.PostfixAllOverloads(mgr, "AddEntity", OnEntityAdded); }
        catch (Exception ex) { WarnOnce("appearhook", "entity-appear hook failed: " + ex.Message); }
    }

    // Postfix on ZEntityMgr.AddEntity(ZEntity, EAppearType, bool); args[0] is the new entity.
    private void OnEntityAdded(object? _, object?[] args)
    {
        if (!_frozen || args.Length == 0 || args[0] is not { } entity) return;
        try
        {
            var uuid = _entities.Uuid(entity);
            if (_appeared.Exists(a => a.Uuid == uuid)) return;
            var kind = _entities.EntType(entity);
            // The local player and their own mount never join the appear re-check (scene-stays spec § 3, review I1).
            var controller = kind == FreezeKinds.Vehicle ? _entities.VehicleController(entity) : 0L;
            var admitted = FreezeTargets.AdmitAppeared(_entities, _ledger, uuid, kind, controller);
            RekeyComp(entity, uuid, kind, admitted);
            if (!admitted)
            {
                if (kind == FreezeKinds.Vehicle) OnExcluded(uuid, kind, "own mount appeared");
                return;
            }
            // Pass the hook's own entity through (not just the uuid): recon only proves the model is live at the
            // AddEntity postfix, not that a GetEntity(uuid) re-lookup already finds it (Task 9 round 2).
            FreezeFactor(uuid, entity);
            if (_holding && HoldOrigin() is { } origin) TryHold(uuid, origin, entity);
            _appeared.Add((uuid, kind, 0));
            OnAppearFrozen(uuid, kind);
            SyncLate();
        }
        catch (Exception ex) { WarnOnce("appear", "could not freeze an appearing entity: " + ex.Message); }
    }

    /// <summary>The appearing entity's anim component follows its NEW owner in the gate: tracked under
    /// <paramref name="uuid"/> when admitted, forgotten when refused (a recycled component must not stay keyed to the dead
    /// entity — its init write would be substituted under the dead uuid and the new entity left at 0 after unfreeze).</summary>
    private void RekeyComp(object entity, long uuid, int kind, bool admitted)
    {
        if (!ResolveDrawnSpeed() || _entities.LiveModel(entity) is not { } m || _animComp!(m) is not { } comp) return;
        _speedGate.Rekey(CompPointer(comp), uuid, kind, admitted);
    }

    /// <summary>One late frame of re-checks; an entity leaves the watch when its watch ends, when it despawns, or when its
    /// re-check throws.</summary>
    private void TickAppeared()
    {
        for (var i = _appeared.Count - 1; i >= 0; i--)
        {
            var (uuid, kind, frames) = _appeared[i];
            frames++;
            if (!TickOneAppeared(uuid, kind, frames)) { _appeared.RemoveAt(i); continue; }
            _appeared[i] = (uuid, kind, frames);
        }
    }

    /// <summary>One appeared entity's re-check for one late frame. False once it should leave the watch — isolated in its
    /// own try so a failing entity is dropped from the watch instead of aborting the rest of <c>_appeared</c>.</summary>
    private bool TickOneAppeared(long uuid, int kind, int frames)
    {
        try
        {
            if (frames >= AppearWatchFrames || _ledger.Excludes(uuid) || _entities.EntityByUuid(uuid) is not { } entity) return false;
            if (frames >= Stage2Delay && ResolveDrawnSpeed()) ReapplyIfResumed(uuid, kind, entity);
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce("appeartick", "could not re-check an appearing entity: " + ex.Message);
            return false;
        }
    }

    /// <summary>The entity's live anim component, re-checked by <see cref="ReapplyComp"/> (one pointer read, one speed
    /// read).</summary>
    private void ReapplyIfResumed(long uuid, int kind, object entity)
    {
        if (_entities.LiveModel(entity) is not { } m || _animComp!(m) is not { } comp) return;
        ReapplyComp(uuid, kind, entity, comp);
    }
}
