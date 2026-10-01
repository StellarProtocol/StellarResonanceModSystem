using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>Entities that appear while frozen (recon run 3 R3-10). A postfix on <c>ZEntityMgr.AddEntity</c> — main thread,
/// model already live — applies stage 1 and, while the hold is on, holds the entity. For the next
/// <see cref="AppearWatchFrames"/> late frames its drawn speed is re-checked; while above 0 the attr write is re-applied
/// (the game resets some players' factor right after <c>AddEntity</c>) and stage 2 is applied. Event-driven: the hook
/// returns at once unless frozen. <c>onAddEntity</c> and <c>OnModelLoadFinish</c> never fired in the probe and are not
/// hooked. <see cref="TickOneAppeared"/> isolates each watched entity's re-check in its own try so one failure drops
/// only that entity from the watch, never the rest (review finding, Task 9 round 1).</summary>
internal sealed partial class GameFreezeBackend
{
    private const int AppearWatchFrames = 30;

    private readonly List<(long Uuid, int Frames)> _appeared = new();

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
            if (uuid == _entities.PlayerUuid() || _appeared.Exists(a => a.Uuid == uuid)) return;
            FreezeFactor(uuid);
            if (_holding && HoldOrigin() is { } origin) TryHold(uuid, origin);
            _appeared.Add((uuid, 0));
            OnAppearFrozen(uuid, _entities.EntType(entity));
            SyncLate();
        }
        catch (Exception ex) { WarnOnce("appear", "could not freeze an appearing entity: " + ex.Message); }
    }

    /// <summary>One late frame of re-checks; an entity leaves the watch after <see cref="AppearWatchFrames"/> frames,
    /// when it despawns, or when its re-check throws.</summary>
    private void TickAppeared()
    {
        for (var i = _appeared.Count - 1; i >= 0; i--)
        {
            var (uuid, frames) = _appeared[i];
            frames++;
            if (!TickOneAppeared(uuid, frames)) { _appeared.RemoveAt(i); continue; }
            _appeared[i] = (uuid, frames);
        }
    }

    /// <summary>One appeared entity's re-check for one late frame. False once it should leave the watch: gone,
    /// <see cref="AppearWatchFrames"/> elapsed, or an interop failure — isolated in its own try so a failing entity
    /// is dropped from the watch instead of aborting the rest of <c>_appeared</c> this frame.</summary>
    private bool TickOneAppeared(long uuid, int frames)
    {
        try
        {
            if (frames >= AppearWatchFrames || _entities.EntityByUuid(uuid) is not { } entity) return false;
            if (frames >= Stage2Delay && ResolveDrawnSpeed() && DrawnSpeed(uuid) > FreezeLedger.SpeedEpsilon)
            {
                if (_ledger.Factors.ContainsKey(uuid)) ReapplyFactor(entity);
                FreezeDrawnSpeed(uuid);
            }
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce("appeartick", "could not re-check an appearing entity: " + ex.Message);
            return false;
        }
    }
}
