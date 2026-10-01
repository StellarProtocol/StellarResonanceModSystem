using System;
using System.Reflection;
using Stellar.Abstractions.Services;
namespace Stellar.Infrastructure.Game;

/// <summary>Animation in two stages (recon run 2 A / P5a, run 3 R3-1..R3-4). Stage 1, on every entity whose kind
/// supports it: <c>SetAttrSkillStageTimeFactor(e, 0)</c> + <c>SetAttrAnimSpeedDirty(e, true)</c> + <c>tryCalculateAnimSpeed(e)</c>,
/// the prior value from <c>GetAttrSkillStageTimeFactor</c> — enough for players, vanity pets and mounts. Stage 2, two late
/// frames later: <c>ZModel.AnimComp.Speed = 0</c> on every entity whose drawn speed is still above 0 — NPCs and pets, whose
/// drawn speed the attr never reaches and the game never rewrites. The local player is included (the pose freezes too).</summary>
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
        foreach (var uuid in _ids)
            if (_entities.EntityByUuid(uuid) is { } e) FreezeFactor(uuid, e);
    }

    /// <summary>Stage 1 for one entity. Skipped for kinds where the attr path throws (<see cref="FreezeKinds.AttrSupported"/>).</summary>
    private void FreezeFactor(long uuid, object entity)
    {
        if (_recalc is null || _ledger.Factors.ContainsKey(uuid) || !FreezeKinds.AttrSupported(_entities.EntType(entity))) return;
        try
        {
            var prior = Convert.ToSingle(_getFactor!.Invoke(null, new[] { entity }));
            _setFactor!.Invoke(null, new object[] { entity, FreezeLedger.FrozenFactor });
            _ledger.SaveFactor(uuid, prior);   // saved before the recalc, so a recalc failure is still restored
            Recalc(entity);
        }
        catch (Exception ex) { WarnOnce("animone", "could not freeze an entity's animation: " + (ex.InnerException ?? ex).Message); }
    }

    /// <summary>Writes the frozen factor again (the ledger keeps the first prior). For entities the game reset after
    /// <c>AddEntity</c> (run 3).</summary>
    private void ReapplyFactor(object entity)
    {
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

    /// <summary>Stage 2 for one entity: a drawn speed above 0 becomes 0, the prior kept. True when written.</summary>
    private bool FreezeDrawnSpeed(long uuid)
    {
        if (_entities.LiveModel(_entities.EntityByUuid(uuid)) is not { } m || _animComp!(m) is not { } comp) return false;
        try
        {
            var speed = _getSpeed!(comp);
            if (speed <= FreezeLedger.SpeedEpsilon) return false;
            _ledger.SaveSpeed(uuid, speed);
            _setSpeed!(comp, 0f);
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce("speedone", "could not freeze an entity's drawn speed: " + ex.Message);
            return false;
        }
    }

    /// <summary>The entity's drawn speed, or 0 when it has no model / anim component.</summary>
    private float DrawnSpeed(long uuid)
    {
        if (_entities.LiveModel(_entities.EntityByUuid(uuid)) is not { } m || _animComp!(m) is not { } comp) return 0f;
        try { return _getSpeed!(comp); }
        catch { return 0f; }
    }

    private void UnfreezeAnimation()
    {
        RestoreSpeeds();   // drawn speeds first, then the factors (players' speed is recomputed from the factor)
        foreach (var kv in _ledger.Factors)
        {
            if (_entities.EntityByUuid(kv.Key) is not { } entity) continue;   // left / despawned: nothing to restore
            try
            {
                var current = Convert.ToSingle(_getFactor!.Invoke(null, new[] { entity }));
                if (FreezeLedger.RestoreValue(kv.Value, current) is not float restore) continue;
                _setFactor!.Invoke(null, new object[] { entity, restore });
                Recalc(entity);
            }
            catch (Exception ex) { WarnOnce("animrestore", "could not restore an entity's animation: " + (ex.InnerException ?? ex).Message); }
        }
    }

    private void RestoreSpeeds()
    {
        if (_ledger.Speeds.Count == 0 || _setSpeed is null) return;
        foreach (var kv in _ledger.Speeds)
        {
            if (_entities.LiveModel(_entities.EntityByUuid(kv.Key)) is not { } m || _animComp!(m) is not { } comp) continue;
            try
            {
                if (FreezeLedger.RestoreSpeed(kv.Value, _getSpeed!(comp)) is float restore) _setSpeed(comp, restore);
            }
            catch (Exception ex) { WarnOnce("speedrestore", "could not restore an entity's drawn speed: " + ex.Message); }
        }
    }

    private void Recalc(object entity)
    {
        _animDirty!.Invoke(null, new object[] { entity, true });
        _recalc!.Invoke(null, new[] { entity });
    }
}
