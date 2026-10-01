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
/// hooked.</summary>
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
            FreezeFactor(uuid, entity);
            if (_holding && HoldOrigin() is { } origin) TryHold(uuid, entity, origin);
            _appeared.Add((uuid, 0));
            OnAppearFrozen(uuid, _entities.EntType(entity));
            SyncLate();
        }
        catch (Exception ex) { WarnOnce("appear", "could not freeze an appearing entity: " + ex.Message); }
    }

    /// <summary>One late frame of re-checks; an entity leaves the watch after <see cref="AppearWatchFrames"/> frames or
    /// when it despawns.</summary>
    private void TickAppeared()
    {
        for (var i = _appeared.Count - 1; i >= 0; i--)
        {
            var (uuid, frames) = _appeared[i];
            frames++;
            if (frames >= AppearWatchFrames || _entities.EntityByUuid(uuid) is not { } entity)
            {
                _appeared.RemoveAt(i);
                continue;
            }
            _appeared[i] = (uuid, frames);
            if (frames < Stage2Delay || !ResolveDrawnSpeed() || DrawnSpeed(uuid) <= FreezeLedger.SpeedEpsilon) continue;
            if (_ledger.Factors.ContainsKey(uuid)) ReapplyFactor(entity);
            FreezeDrawnSpeed(uuid);
        }
    }
}
