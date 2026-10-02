using System;
using System.Reflection;
using Stellar.Abstractions.Services;
namespace Stellar.Infrastructure.Game;

/// <summary>Animation in two stages (recon run 2 A / P5a, run 3 R3-1..R3-4). Stage 1, on every entity whose kind
/// supports it: <c>SetAttrSkillStageTimeFactor(e, 0)</c> + <c>SetAttrAnimSpeedDirty(e, true)</c> + <c>tryCalculateAnimSpeed(e)</c>,
/// the prior value from <c>GetAttrSkillStageTimeFactor</c> — enough for players, vanity pets and mounts. Stage 2, two late
/// frames later: <c>ZModel.AnimComp.Speed = 0</c> on every entity whose drawn speed is still above 0 — NPCs and pets, whose
/// drawn speed the attr never reaches. Stage 2 also tracks every entity's anim component for the <c>set_Speed</c> gate
/// (<see cref="DrawnSpeedGate"/>), which keeps the drawn speed at 0 when the GAME rewrites it later (combat, owner report
/// 2026-10-02). The local player and their own mount are never frozen (scene-stays spec § 3, review I1:
/// <see cref="FreezeLedger.Excludes"/>) — they walk and their own emote plays while the rest of the scene holds.
/// Every per-entity lookup below runs inside that entity's own try: one entity's interop failure is caught, warned
/// once and skipped, and never aborts the loop over the rest of <c>_ids</c> — a prior bug here could abort mid-loop,
/// leaving the remaining entities both unfrozen AND un-tracked by <see cref="FreezeLedger"/>, so a later unfreeze
/// could never restore them either (review finding, Task 9 round 1).</summary>
internal sealed partial class GameFreezeBackend
{
    private MethodInfo? _getFactor, _setFactor, _animDirty, _recalc;
    private Func<object, object?>? _animComp;
    private Func<object, float>? _getSpeed;
    private Action<object, float>? _setSpeed;

    private bool ResolveAnimation()
    {
        if (_recalc is not null) return true;
        var ext = _types.FindType(GameEntityAccess.AttrExtType);
        if (ext is null) return false;
        _getFactor = StellarInterop.FindMethod(ext, "GetAttrSkillStageTimeFactor", 1);
        _setFactor = StellarInterop.FindMethod(ext, "SetAttrSkillStageTimeFactor", 2);
        _animDirty = StellarInterop.FindMethod(ext, "SetAttrAnimSpeedDirty", 2);
        var recalc = StellarInterop.FindMethod(ext, "tryCalculateAnimSpeed", 1);
        if (_getFactor is null || _setFactor is null || _animDirty is null || recalc is null) return false;
        _recalc = recalc;
        return true;
    }

    // ZModel.AnimComp (the anim component interface) → Speed, resolved from the property's declared type.
    private bool ResolveDrawnSpeed()
    {
        if (_setSpeed is not null) return true;
        var model = _types.FindType(GameEntityAccess.ModelType);
        var compProp = model is null ? null : StellarInterop.FindPropertyUp(model, "AnimComp");
        var speed = compProp is null ? null : StellarInterop.FindPropertyUp(compProp.PropertyType, "Speed");
        var get = FastAccess.Getter<float>(speed);
        var set = FastAccess.Setter<float>(speed);
        _animComp = FastAccess.Getter<object?>(compProp);
        if (_animComp is null || get is null || set is null) return false;
        _getSpeed = get;
        _setSpeed = set;
        return true;
    }

    private void FreezeAnimation()
    {
        if (!ResolveAnimation()) { WarnOnce("anim", "animation freeze unavailable on this client"); return; }
        foreach (var uuid in _ids) FreezeFactor(uuid);
    }

    /// <summary>Stage 1 for one entity, looked up fresh by uuid. See the <c>(uuid, entity)</c> overload.</summary>
    private void FreezeFactor(long uuid) => FreezeFactor(uuid, null);

    /// <summary>Stage 1 for one entity. Skipped for kinds where the attr path throws
    /// (<see cref="FreezeKinds.AttrSupported"/>). When <paramref name="entity"/> is given — a hook's own postfix
    /// argument, already proven live at the hook site (recon run 3) — it is used directly under the same
    /// <see cref="GameEntityAccess.Live"/> check as a fresh lookup, instead of re-finding it via
    /// <c>GetEntity(uuid)</c>, which recon never proved succeeds at that same instant (Task 9 round 2). Falls back
    /// to the uuid lookup when null. Every interop read runs inside this one try, so this entity's failure is
    /// caught and skipped without aborting the caller's loop over the rest.</summary>
    private void FreezeFactor(long uuid, object? entity)
    {
        if (_recalc is null || _ledger.Excludes(uuid) || _ledger.Factors.ContainsKey(uuid)) return;
        try
        {
            var live = entity is not null ? _entities.Live(entity) : _entities.EntityByUuid(uuid);
            if (live is not { } e || !FreezeKinds.AttrSupported(_entities.EntType(e))) return;
            var prior = Convert.ToSingle(_getFactor!.Invoke(null, new[] { e }));
            // Saved BEFORE the write: the ledger refuses the local player (never frozen), and a write that then throws
            // leaves a prior the restore skips (it only restores while our frozen value is still in place).
            if (!_ledger.SaveFactor(uuid, prior)) return;
            _setFactor!.Invoke(null, new object[] { e, FreezeLedger.FrozenFactor });
            Recalc(e);
        }
        catch (Exception ex) { WarnOnce("animone", "could not freeze an entity's animation: " + (ex.InnerException ?? ex).Message); }
    }

    /// <summary>Writes the frozen factor again (the ledger keeps the FIRST prior — a re-apply never touches it). For
    /// entities the game reset after <c>AddEntity</c> (run 3).</summary>
    private void ReapplyFactor(long uuid, object entity)
    {
        if (_ledger.Excludes(uuid)) return;
        try
        {
            _setFactor!.Invoke(null, new object[] { entity, FreezeLedger.FrozenFactor });
            Recalc(entity);
        }
        catch (Exception ex) { WarnOnce("animre", "could not re-freeze an entity's animation: " + (ex.InnerException ?? ex).Message); }
    }

    /// <summary>Stage 2 over the entities of this freeze press.</summary>
    private void FreezeDrawnSpeeds()
    {
        if (!ResolveDrawnSpeed()) { WarnOnce("speed", "NPC and pet animation freeze unavailable on this client"); return; }
        var n = 0;
        foreach (var uuid in _ids)
            if (FreezeDrawnSpeed(uuid)) n++;
        OnStage2(n);
    }

    /// <summary>Stage 2 for one entity: tracks its anim component for the <c>set_Speed</c> gate, and a drawn speed
    /// above 0 becomes 0 (<see cref="FreezeLedger.AdmitSpeed"/> keeps the restore value). True when written. The
    /// entity lookup and the write both run inside the same try, so a failed lookup is caught here instead of
    /// aborting <see cref="FreezeDrawnSpeeds"/>'s loop over the rest of <c>_ids</c>.</summary>
    private bool FreezeDrawnSpeed(long uuid)
    {
        if (_ledger.Excludes(uuid)) return false;   // no model read for an excluded entity at all
        try
        {
            if (_entities.EntityByUuid(uuid) is not { } e || _entities.LiveModel(e) is not { } m || _animComp!(m) is not { } comp) return false;
            return FreezeComp(uuid, _entities.EntType(e), comp);
        }
        catch (Exception ex)
        {
            WarnOnce("speedone", "could not freeze an entity's drawn speed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Tracks <paramref name="comp"/> for the gate and writes 0 over a drawn speed above 0 — every time, also on
    /// a re-apply after a game rewrite (regression <c>freeze_combat_resume_reapply</c>). True when written.</summary>
    private bool FreezeComp(long uuid, int kind, object comp)
    {
        _speedGate.Track(CompPointer(comp), uuid, kind);
        var speed = _getSpeed!(comp);
        if (speed <= FreezeLedger.SpeedEpsilon || !_ledger.AdmitSpeed(uuid, speed)) return false;
        WriteSpeed(comp, 0f);
        return true;
    }

    /// <summary>The appear re-check for one late frame: tracks <paramref name="comp"/> for the gate, reads its drawn
    /// speed ONCE (perf review) and, while above 0 (the game restarted it), keeps that speed as the latest restore value,
    /// re-applies the frozen factor (first prior kept; its recalc's own <c>set_Speed</c> is gated — tracked first — and
    /// a newer game value it writes wins, being noted after) and writes 0 again.</summary>
    private void ReapplyComp(long uuid, int kind, object entity, object comp)
    {
        _speedGate.Track(CompPointer(comp), uuid, kind);
        var speed = _getSpeed!(comp);
        if (speed <= FreezeLedger.SpeedEpsilon || !_ledger.AdmitSpeed(uuid, speed)) return;
        if (_ledger.Factors.ContainsKey(uuid)) ReapplyFactor(uuid, entity);
        WriteSpeed(comp, 0f);
    }

    private void Recalc(object entity)
    {
        _animDirty!.Invoke(null, new object[] { entity, true });
        _recalc!.Invoke(null, new[] { entity });
    }
}
