using System;
using System.Collections.Generic;
using System.Diagnostics;
using Stellar.Abstractions.Services;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>Position hold (recon run 2 C W2, run 3 R3-9): every LateUpdate, write each held entity's visual
/// <c>ModelGoComp.Position</c> back to where it was drawn at freeze time (2.8–4.0 µs per entity). Held = every movable kind
/// (<see cref="FreezeKinds.Movable"/>) within <see cref="FreezeKinds.HoldRadius"/> of the local player, never the local
/// player or their own mount (<see cref="FreezeTargets.MayHold"/>, review I1/I2). The logical position keeps moving underneath; on release each held model is written back to its logical
/// position (a pet stayed 18.48 m off for 3+ frames otherwise). Self-disables over <see cref="HoldBudget.LimitMs"/>.
/// <see cref="TryHold"/> and <see cref="SnapHeldToLogical"/> isolate each entity's lookup in its own try so one bad
/// entity never skips the rest (review finding, Task 9 round 1).</summary>
internal sealed partial class GameFreezeBackend
{
    internal const string ModelGoCompType = "Panda.ZGame.ModelGoCompBase";

    private readonly List<(long Uuid, Vector3 Pos)> _held = new();
    private readonly HoldBudget _budget = new();
    private readonly Stopwatch _holdWatch = new();
    private Func<object, object?>? _goComp;
    private Func<object, Vector3>? _getPos;
    private Action<object, Vector3>? _setPos;
    // Per-frame lookup, compiled: ZEntityMgr.GetEntity(uuid) (null once culled — docs/il2cpp-probing-safety.md § 3),
    // ZEntity.IsDestroying / Model, ZModel.IsDestroying — the same gates as GameEntityAccess, without MethodInfo.Invoke.
    private Func<object, long, object?>? _fastEntity;
    private Func<object, bool>? _entGone, _modelGone;
    private Func<object, object?>? _entModel;
    private bool _holding;

    private bool ResolveHold()
    {
        if (_setPos is not null) return true;
        var model = _types.FindType(GameEntityAccess.ModelType);
        var comp = _types.FindType(ModelGoCompType);
        if (model is null || comp is null || !ResolveHoldLookup(model)) return false;
        _goComp = FastAccess.Getter<object?>(StellarInterop.FindPropertyUp(model, "ModelGoComp"));
        var position = StellarInterop.FindPropertyUp(comp, "Position");
        _getPos = FastAccess.Getter<Vector3>(position);
        var set = FastAccess.Setter<Vector3>(position);
        if (_goComp is null || _getPos is null || set is null) return false;
        _setPos = set;
        return true;
    }

    private bool ResolveHoldLookup(Type model)
    {
        var mgr = _types.FindType(GameEntityAccess.ManagerType);
        var ent = _types.FindType(GameEntityAccess.EntityType);
        if (mgr is null || ent is null) return false;
        _fastEntity = FastAccess.Func1<long, object?>(StellarInterop.FindMethod(mgr, "GetEntity", 1));
        _entGone = FastAccess.Getter<bool>(StellarInterop.FindPropertyUp(ent, "IsDestroying"));
        _entModel = FastAccess.Getter<object?>(StellarInterop.FindPropertyUp(ent, "Model"));
        _modelGone = FastAccess.Getter<bool>(StellarInterop.FindPropertyUp(model, "IsDestroying"));
        return _fastEntity is not null && _entGone is not null && _entModel is not null && _modelGone is not null;
    }

    /// <summary>The held entity's live <c>ModelGoComp</c> this frame, or null when it left, is despawning or has no model.</summary>
    private object? HeldComp(object mgr, long uuid)
    {
        if (_fastEntity!(mgr, uuid) is not { } e || _entGone!(e) || _entModel!(e) is not { } m || _modelGone!(m)) return null;
        return _goComp!(m);
    }

    private void StartHold()
    {
        if (!ResolveHold()) { WarnOnce("hold", "position hold unavailable on this client"); return; }
        _held.Clear();
        if (HoldOrigin() is not { } origin) return;
        foreach (var uuid in _ids) TryHold(uuid, origin);   // the local player is already out of _ids
        if (_held.Count == 0) return;
        _budget.Reset();
        _holding = true;
    }

    /// <summary>The local player's logical position (the hold radius centre), or null outside the world.</summary>
    private Vector3? HoldOrigin() =>
        _entities.LiveModel(_entities.LocalEntity()) is { } me ? _entities.AttrPosition(me) : null;

    /// <summary>Holds <paramref name="uuid"/> at its drawn position, looked up fresh by uuid. See the
    /// <c>(uuid, origin, entity)</c> overload.</summary>
    private void TryHold(long uuid, Vector3 origin) => TryHold(uuid, origin, null);

    /// <summary>Holds <paramref name="uuid"/> at its drawn position when it is a movable kind within the radius.
    /// When <paramref name="entity"/> is given — a hook's own postfix argument, already proven live at the hook
    /// site (recon run 3) — it is used directly under the same <see cref="GameEntityAccess.Live"/> check as a
    /// fresh lookup, instead of re-finding it via <c>GetEntity(uuid)</c>, which recon never proved succeeds at
    /// that same instant (Task 9 round 2). Falls back to the uuid lookup when null. The whole lookup runs inside
    /// one try: an interop failure for this entity is caught and warned once, never aborting the caller's loop
    /// over the rest of <c>_ids</c>.</summary>
    private void TryHold(long uuid, Vector3 origin, object? entity)
    {
        if (_ledger.Excludes(uuid)) return;   // never the local player or their own mount: no read at all
        try
        {
            var live = entity is not null ? _entities.Live(entity) : _entities.EntityByUuid(uuid);
            if (live is not { } e || _entities.LiveModel(e) is not { } m || _entities.AttrPosition(m) is not { } at) return;
            var held = _held.Exists(h => h.Uuid == uuid);   // re-appeared while still held
            if (!FreezeTargets.MayHold(_ledger, uuid, _entities.EntType(e), Vector3.Distance(at, origin), held)) return;
            if (_goComp!(m) is { } comp) _held.Add((uuid, _getPos!(comp)));
        }
        catch (Exception ex) { WarnOnce("holdone", "could not hold an entity's position: " + ex.Message); }
    }

    /// <summary>Releases one held entity mid-freeze (it turned out to be excluded): snapped to its logical position, out
    /// of the hold.</summary>
    private void Unhold(long uuid)
    {
        var i = _held.FindIndex(h => h.Uuid == uuid);
        if (i < 0) return;
        try
        {
            if (_entities.LiveModel(_entities.EntityByUuid(uuid)) is { } m && _goComp!(m) is { } comp && _entities.AttrPosition(m) is { } logical)
                _setPos!(comp, logical);
        }
        catch (Exception ex) { WarnOnce("holdsnap", "could not snap a held entity back: " + ex.Message); }
        _held.RemoveAt(i);
    }

    private void StopHold()
    {
        if (!_holding) return;
        _holding = false;
        SnapHeldToLogical();
        _held.Clear();
    }

    // Release snap (run 3 R3-9): the drawn model rejoins its logical position on this frame, not whenever it next moves.
    private void SnapHeldToLogical()
    {
        foreach (var (uuid, _) in _held)
        {
            try
            {
                if (_entities.LiveModel(_entities.EntityByUuid(uuid)) is not { } m || _goComp!(m) is not { } comp) continue;
                if (_entities.AttrPosition(m) is { } logical) _setPos!(comp, logical);
            }
            catch (Exception ex) { WarnOnce("holdsnap", "could not snap a held entity back: " + ex.Message); }
        }
    }

    private void HoldTick()
    {
        _holdWatch.Restart();
        try
        {
            if (_entities.Manager() is not { } mgr) return;   // no entity manager this frame: nothing to hold
            foreach (var (uuid, pos) in _held)
                if (HeldComp(mgr, uuid) is { } comp) _setPos!(comp, pos);
        }
        catch (Exception ex)
        {
            WarnOnce("holdtick", "position hold stopped after an error: " + ex.Message);
            DisableHold();
            return;
        }
        _holdWatch.Stop();
        if (!_budget.Record(_holdWatch.Elapsed.TotalMilliseconds)) return;
        _log.Info($"{Tag}position hold off: {_budget.LastAverageMs:F3} ms/frame over {HoldBudget.Window} frames " +
                  $"for {_held.Count} entities (budget {HoldBudget.LimitMs} ms); animation and effects stay frozen");
        DisableHold();
    }

    private void DisableHold()
    {
        StopHold();
        HoldDisabled?.Invoke();
    }
}
