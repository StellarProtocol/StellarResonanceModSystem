using System;
using System.Collections.Generic;
using System.Diagnostics;
using Stellar.Abstractions.Services;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>Position + rotation hold (recon run 2 C W2, run 3 R3-9; rotation: owner MAIN evidence 2026-10-02 — a frozen
/// boss kept turning under <c>MoveComp.RotGo</c>): every LateUpdate, write each held entity's visual
/// <c>ModelGoComp.Position</c> AND <c>Rotation</c> back to the pose it was drawn at freeze time (<see cref="PoseHold{TPos,TRot}"/>;
/// 2.8–4.0 µs per entity for the position). Both are <c>ModelGoCompBase</c> virtuals: on an ECS model they are
/// <c>ECSModelGoComp</c>'s overrides, whose getters read the bound bone transform the renderer draws from (release_3.7 ISIL)
/// — the hold reads and writes the rendered root. Held = every movable kind
/// (<see cref="FreezeKinds.Movable"/>) within <see cref="FreezeKinds.HoldRadius"/> of the local player, never the local
/// player or their own mount (<see cref="FreezeTargets.MayHold"/>, review I1/I2). The logical pose keeps moving underneath; on release each held model is written back to its logical
/// position and rotation (a pet stayed 18.48 m off for 3+ frames otherwise). Self-disables over <see cref="HoldBudget.LimitMs"/>.
/// <see cref="TryHold"/> and the release isolate each entity's lookup in its own try so one bad
/// entity never skips the rest (review finding, Task 9 round 1).</summary>
internal sealed partial class GameFreezeBackend
{
    internal const string ModelGoCompType = "Panda.ZGame.ModelGoCompBase";

    private readonly PoseHold<Vector3, Quaternion> _held = new();
    private readonly HoldBudget _budget = new();
    private readonly Stopwatch _holdWatch = new();
    private Func<object, object?>? _goComp;
    private Func<object, Vector3>? _getPos;
    private Action<object, Vector3>? _setPos;
    private Func<object, Quaternion>? _getRot;
    private Action<object, Quaternion>? _setRot;
    // Per-frame lookup, compiled: ZEntityMgr.GetEntity(uuid) (null once culled — docs/il2cpp-probing-safety.md § 3),
    // ZEntity.IsDestroying / Model, ZModel.IsDestroying — the same gates as GameEntityAccess, without MethodInfo.Invoke.
    private Func<object, long, object?>? _fastEntity;
    private Func<object, bool>? _entGone, _modelGone;
    private Func<object, object?>? _entModel;
    private Func<long, object?>? _tickComp;
    private Func<long, (object Comp, Vector3 Pos, Quaternion? Rot)?>? _logicalPose;
    private Action<Exception>? _snapError;
    private object? _tickMgr;
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
        var rotation = StellarInterop.FindPropertyUp(comp, "Rotation");
        _getRot = FastAccess.Getter<Quaternion>(rotation);
        _setRot = _getRot is null ? null : FastAccess.Setter<Quaternion>(rotation);   // no rotation: positions still hold
        var set = FastAccess.Setter<Vector3>(position);
        if (_goComp is null || _getPos is null || set is null) return false;
        _tickComp = uuid => _tickMgr is { } mgr ? HeldComp(mgr, uuid) : null;
        _logicalPose = LogicalPose;
        _snapError = ex => WarnOnce("holdsnap", "could not snap a held entity back: " + ex.Message);
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
            var held = _held.Contains(uuid);   // re-appeared while still held
            if (!FreezeTargets.MayHold(_ledger, uuid, _entities.EntType(e), Vector3.Distance(at, origin), held)) return;
            if (_goComp!(m) is { } comp) _held.Add(uuid, _getPos!(comp), ReadRotation(comp));
        }
        catch (Exception ex) { WarnOnce("holdone", "could not hold an entity's position: " + ex.Message); }
    }

    /// <summary>The drawn rotation to hold, or null when this client cannot read it (then it is never written).</summary>
    private Quaternion? ReadRotation(object comp)
    {
        if (_getRot is null || _setRot is null) return null;
        try { return _getRot(comp); }
        catch { return null; }
    }

    /// <summary>A held entity's live drawn component and its LOGICAL pose (<c>GetAttrGoPosition</c> / <c>GetAttrGoRotation</c>),
    /// or null when it left — the release snap's lookup.</summary>
    private (object Comp, Vector3 Pos, Quaternion? Rot)? LogicalPose(long uuid)
    {
        if (_entities.LiveModel(_entities.EntityByUuid(uuid)) is not { } m || _goComp!(m) is not { } comp) return null;
        return _entities.AttrPosition(m) is { } logical ? (comp, logical, _entities.AttrRotation(m)) : null;
    }

    /// <summary>Releases one held entity mid-freeze (it turned out to be excluded, or a deferred removal replays): snapped
    /// to its logical pose, out of the hold.</summary>
    private void Unhold(long uuid)
    {
        if (!_held.Contains(uuid)) return;
        PoseHold<Vector3, Quaternion>.Snap(uuid, _logicalPose!, _setPos!, _setRot, _snapError!);
        _held.Remove(uuid);
    }

    private void StopHold()
    {
        if (!_holding) return;
        _holding = false;
        // Release snap (run 3 R3-9): the drawn model rejoins its logical pose on this frame, not whenever it next moves.
        _held.Release(_logicalPose!, _setPos!, _setRot, _snapError!);
        _held.Clear();
    }

    private void HoldTick()
    {
        _holdWatch.Restart();
        try
        {
            if (_entities.Manager() is not { } mgr) return;   // no entity manager this frame: nothing to hold
            _tickMgr = mgr;
            _held.Tick(_tickComp!, _setPos!, _setRot);
        }
        catch (Exception ex)
        {
            _tickMgr = null;
            WarnOnce("holdtick", "position hold stopped after an error: " + ex.Message);
            DisableHold();
            return;
        }
        _tickMgr = null;   // never kept across frames
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
