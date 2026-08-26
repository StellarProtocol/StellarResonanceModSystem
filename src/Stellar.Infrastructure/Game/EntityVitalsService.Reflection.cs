using System;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection bootstrap + live-read/watcher plumbing for <see cref="EntityVitalsService"/>. Split out
/// so the main file stays under the analyzer's file-size gate. Mirrors
/// <see cref="EntityTransformsService"/>'s bootstrap shape (I-1: guard is handle-presence, not a
/// permanent bool, so a failed attempt on a not-yet-loaded hot-update assembly retries next call).
/// </summary>
internal sealed partial class EntityVitalsService
{
    // -------------------------------------------------------------------------
    // Bootstrap
    // -------------------------------------------------------------------------

    private void EnsureResolved()
    {
        if (_mgrInstanceProperty is not null && _getEntityMethod is not null
            && _conversionMethod is not null && _isBossProperty is not null)
        {
            return;
        }
        try { TryResolveHandles(); }
        catch { /* leave unresolved — retries next call */ }
    }

    private void TryResolveHandles()
    {
        var mgrType = _typeRegistry.FindType(ManagerTypeName);
        var entityType = _typeRegistry.FindType(EntityTypeName);
        var bloodUtilType = _typeRegistry.FindType(BloodUtilTypeName);
        if (mgrType is null || entityType is null || bloodUtilType is null) return;

        var instanceProp = FindSingletonInstanceProperty(mgrType);
        var getEntity = mgrType.GetMethod("GetEntity", AnyInstance, binder: null, types: new[] { typeof(long) }, modifiers: null);
        var conversion = bloodUtilType.GetMethod(
            "ConversionBloodLogicDataToViewData", AnyStatic, binder: null, types: new[] { entityType }, modifiers: null);
        var isBossProp = entityType.GetProperty("IsBoss", AnyInstance);
        if (instanceProp is null || getEntity is null || conversion is null || isBossProp is null) return;

        // Resolved in the SAME deterministic pass as the core four handles above, NOT independently
        // retried — EnsureResolved's guard only checks the core four, so once those resolve this method
        // stops being called again and a handle missed here (mgrType resolved, but e.g. IsEntityExist's
        // signature didn't match) never gets a second attempt. That's fine for IsEntityExist/
        // IsEntityActive specifically: MANDATORY for IsLive to ever return true (I3), so a partial
        // failure here is permanent and surfaces as the tap staying inert + DiagLivenessGateMissing's
        // one-shot warning — not a silent degrade. The watcher pair stays genuinely optional either way.
        _isEntityExistMethod ??= mgrType.GetMethod("IsEntityExist", AnyInstance, binder: null, types: new[] { typeof(long) }, modifiers: null);
        _isEntityActiveMethod ??= mgrType.GetMethod("IsEntityActive", AnyInstance, binder: null, types: new[] { typeof(long) }, modifiers: null);
        _bindWatcherMethod ??= FindBindWatcherMethod(mgrType);
        _unbindWatcherMethod ??= FindUnbindWatcherMethod(mgrType);

        // Publish the CORE handles atomically so EnsureResolved's guard stays coherent.
        _mgrInstanceProperty = instanceProp;
        _getEntityMethod = getEntity;
        _conversionMethod = conversion;
        _isBossProperty = isBossProp;
    }

    private static MethodInfo? FindBindWatcherMethod(Type mgrType)
    {
        foreach (var m in mgrType.GetMethods(AnyInstance))
        {
            if (m.Name != "BindEntityLuaAttrWatcher") continue;
            var ps = m.GetParameters();
            if (ps.Length == 3 && ps[0].ParameterType == typeof(long)) return m;
        }
        return null;
    }

    private static MethodInfo? FindUnbindWatcherMethod(Type mgrType)
    {
        foreach (var m in mgrType.GetMethods(AnyInstance))
        {
            if (m.Name != "UnbindEntityLuaAttrWater") continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == typeof(long)) return m;
        }
        return null;
    }

    private static PropertyInfo? FindSingletonInstanceProperty(Type tMgr)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? singletonOpen;
            try { singletonOpen = assembly.GetType("ZUtil.ZSingleton`1", throwOnError: false); }
            catch { continue; }
            if (singletonOpen is null) continue;
            try
            {
                var closed = singletonOpen.MakeGenericType(tMgr);
                var prop = closed.GetProperty("Instance", AnyStatic);
                if (prop is not null) return prop;
            }
            catch { /* try next assembly */ }
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // Live read
    // -------------------------------------------------------------------------

    // I3 review fix: IsEntityExist/IsEntityActive used to be "optional, nice-to-have" — if neither
    // handle resolved, the OLD code fell through to `return true`, i.e. an UNGATED GetEntity/
    // reflected-read on every call. That's exactly the native-crash class docs/il2cpp-probing-safety.md
    // warns about (a live IL2CPP deref with no liveness gate). The liveness gate is now MANDATORY:
    // unresolved handles means the whole native tap stays inert (TryGetBlood/IsBoss return false)
    // until both resolve, logged once so the degraded state is visible without STELLAR_DIAGNOSTICS.
    private bool IsLive(long uuid)
    {
        if (_mgrInstanceProperty is null) return false;
        if (_isEntityExistMethod is null || _isEntityActiveMethod is null)
        {
            DiagLivenessGateMissing();
            return false;
        }
        try
        {
            var mgr = _mgrInstanceProperty.GetValue(null);
            if (mgr is null) return false;
            if (_isEntityExistMethod.Invoke(mgr, new object[] { uuid }) is false) return false;
            if (_isEntityActiveMethod.Invoke(mgr, new object[] { uuid }) is false) return false;
            return true;
        }
        catch { return false; }
    }

    private object? ResolveEntity(long uuid)
    {
        if (_mgrInstanceProperty is null || _getEntityMethod is null) return null;
        try
        {
            var mgr = _mgrInstanceProperty.GetValue(null);
            return mgr is null ? null : _getEntityMethod.Invoke(mgr, new object[] { uuid });
        }
        catch { return null; }
    }

    private bool TryReadLive(long uuid, out int percent, out int stage)
    {
        percent = 0;
        stage = 0;
        if (!TryReadLiveResult(uuid, out var result) || result is null) return false;
        ResolveBloodFields(result.GetType());
        percent = ExtractPercent(uuid, result);
        stage = ExtractStage(result);
        return true;
    }

    // Boxed Nullable<BossBloodLogicData>: HasValue=false boxes to a real null reference, HasValue=true
    // boxes directly to the struct — so a null `result` here is a normal, expected "no boss-blood
    // observation yet" answer, not a failure.
    private bool TryReadLiveResult(long uuid, out object? result)
    {
        result = null;
        if (!IsLive(uuid)) return false;
        var entity = ResolveEntity(uuid);
        if (entity is null) return false;
        try
        {
            result = _conversionMethod!.Invoke(null, new object[] { entity });
            return result is not null;
        }
        catch { return false; }
    }

    private void ResolveBloodFields(Type t)
    {
        if (_bloodFieldsResolved) return;
        _bloodFieldsResolved = true;
        _percentField = t.GetField("BloodPercent", AnyInstance);
        if (_percentField is null) _percentProperty = t.GetProperty("BloodPercent", AnyInstance);
        _stageField = t.GetField("Stage", AnyInstance);
        if (_stageField is null) _stageProperty = t.GetProperty("Stage", AnyInstance);
    }

    private int ExtractPercent(long uuid, object result)
    {
        try
        {
            var raw = _percentField is not null ? _percentField.GetValue(result) : _percentProperty?.GetValue(result);
            var f = ToFloat(raw);
            // M4 review fix: the ≤1 "treat as fraction" heuristic below collapses a REAL 1% (the
            // scripted-kill floor value, BossScriptedKillHpFrac=0.15f is the plugin-side threshold but
            // the observed value can be lower) down to 100% if it's actually already a percent — log
            // the raw value once per entity so the acceptance raid settles which scale BloodPercent uses.
            DiagRawBloodPercent(uuid, f);
            return ToPercentInt(f);
        }
        catch { return 0; }
    }

    private int ExtractStage(object result)
    {
        try
        {
            var raw = _stageField is not null ? _stageField.GetValue(result) : _stageProperty?.GetValue(result);
            return ToInt(raw);
        }
        catch { return 0; }
    }

    // UNVERIFIED headless (docs/il2cpp-probing-safety.md — no real game process in CI): whether
    // BloodPercent ships as 0..100 or a 0..1 fraction. Treat a value in (0,1] as a fraction — matches
    // every other percent-ish value in this codebase — else treat it as already a percent. The native-
    // vs-wire diagnostic (grammar line 4) is the acceptance instrument that settles this on the next raid.
    internal static int ToPercentInt(float raw)
    {
        var f = raw > 0f && raw <= 1f ? raw * 100f : raw;
        var i = (int)MathF.Round(f);
        return i < 0 ? 0 : i > 100 ? 100 : i;
    }

    private static int ToInt(object? v) => v switch
    {
        int i => i,
        long l => (int)l,
        float f => (int)f,
        double d => (int)d,
        _ => 0,
    };

    private static float ToFloat(object? v) => v switch
    {
        float f => f,
        double d => (float)d,
        int i => i,
        long l => l,
        _ => 0f,
    };
}
