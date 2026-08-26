using System;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection handles for an Il2CppInterop Nullable&lt;T&gt;-shaped wrapper, resolved once per raw
/// result <see cref="Type"/> by <see cref="EntityVitalsService.GetOrResolveNullableHandles"/>
/// (field-failure review fix, sea/WwLG5Bq4ni, point 1). Property checked before field per member —
/// the interop generator emits properties.
/// </summary>
internal readonly struct NullableWrapperHandles
{
    public readonly PropertyInfo? HasValueProperty;
    public readonly FieldInfo? HasValueField;
    public readonly PropertyInfo? ValueProperty;
    public readonly FieldInfo? ValueField;

    public NullableWrapperHandles(PropertyInfo? hasValueProperty, FieldInfo? hasValueField, PropertyInfo? valueProperty, FieldInfo? valueField)
    {
        HasValueProperty = hasValueProperty;
        HasValueField = hasValueField;
        ValueProperty = valueProperty;
        ValueField = valueField;
    }

    /// <summary>True when the type carries BOTH a bool HasValue member and a Value/value member.</summary>
    public bool IsNullableWrapper => (HasValueProperty is not null || HasValueField is not null)
                                   && (ValueProperty is not null || ValueField is not null);
}

/// <summary>
/// Reflection handles for the <c>BossBloodLogicData</c>-shaped value's <c>BloodPercent</c>/<c>Stage</c>
/// members, resolved once per (unwrapped) value <see cref="Type"/> by
/// <see cref="EntityVitalsService.ResolveBloodFields"/> (field-failure review fix, sea/WwLG5Bq4ni,
/// point 2 — keyed per-Type so a miss for one type is never latched globally).
/// </summary>
internal readonly struct BloodFieldHandles
{
    public readonly FieldInfo? PercentField;
    public readonly PropertyInfo? PercentProperty;
    public readonly FieldInfo? StageField;
    public readonly PropertyInfo? StageProperty;

    public BloodFieldHandles(FieldInfo? percentField, PropertyInfo? percentProperty, FieldInfo? stageField, PropertyInfo? stageProperty)
    {
        PercentField = percentField;
        PercentProperty = percentProperty;
        StageField = stageField;
        StageProperty = stageProperty;
    }

    public bool PercentReadable => PercentField is not null || PercentProperty is not null;
    public bool StageReadable => StageField is not null || StageProperty is not null;
}

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

        var handles = ResolveBloodFields(result.GetType());
        // Point 3 fix (sea/WwLG5Bq4ni): a null/unconvertible percent FAILS the whole tier — it no
        // longer silently degrades to a reported 0%, which is exactly what masked the wire tiers that
        // used to produce correct dungeon tracks (this tier claimed success every tick regardless).
        if (!TryExtractPercent(uuid, result, handles, out percent)) return false;
        stage = ExtractStage(result, handles);
        return true;
    }

    // Point 1 fix (sea/WwLG5Bq4ni): the OLD contract here ("boxed Nullable<BossBloodLogicData>:
    // HasValue=false boxes to a real null reference") does NOT hold across the Il2CppInterop boundary —
    // the interop-generated wrapper is a REAL, non-null object even when HasValue=false. A non-null
    // invoke result must still be probed for a HasValue/Value shape and unwrapped (TryUnwrap) before
    // it's usable; only a genuine null invoke result (or HasValue=false after unwrap) means "no
    // boss-blood observation yet", not a failure.
    private bool TryReadLiveResult(long uuid, out object? result)
    {
        result = null;
        if (!IsLive(uuid)) return false;
        var entity = ResolveEntity(uuid);
        if (entity is null) return false;
        try
        {
            var raw = _conversionMethod!.Invoke(null, new object[] { entity });
            return raw is not null && TryUnwrap(raw, out result);
        }
        catch { result = null; return false; }
    }

    // Detects an Il2CppInterop Nullable<T>-shaped wrapper by reflection (property checked before
    // field — interop emits properties) and unwraps it. A type with no HasValue+Value shape is assumed
    // to already BE the value (preserves the original managed-boxing assumption as a fallback, in case
    // a future interop build DOES box normally). Cached per raw Type (GetOrResolveNullableHandles).
    // internal for direct unit coverage with plain C# test doubles (no IL2Cpp needed — pure reflection
    // over whatever object is passed in).
    internal bool TryUnwrap(object raw, out object? result)
    {
        result = null;
        var rawType = raw.GetType();
        var wrap = GetOrResolveNullableHandles(rawType);
        if (!wrap.IsNullableWrapper)
        {
            result = raw;
            return true;
        }
        bool hasValue;
        try
        {
            var hv = wrap.HasValueProperty is not null ? wrap.HasValueProperty.GetValue(raw) : wrap.HasValueField!.GetValue(raw);
            hasValue = hv is true;
        }
        catch { return false; }
        if (!hasValue) return false; // genuine "no boss-blood observation yet"
        try
        {
            result = wrap.ValueProperty is not null ? wrap.ValueProperty.GetValue(raw) : wrap.ValueField!.GetValue(raw);
            return result is not null;
        }
        catch { result = null; return false; }
    }

    // Resolves + caches (per raw Type) whether that Type looks like a Nullable<T> wrapper: a bool
    // "HasValue" member (property preferred, field fallback) plus a "Value"/"value" member of any type.
    // internal for direct unit coverage with plain C# test doubles (no IL2Cpp needed — pure reflection).
    internal NullableWrapperHandles GetOrResolveNullableHandles(Type t)
    {
        if (_nullableWrapperHandlesByType.TryGetValue(t, out var cached)) return cached;

        var hasValueProp = t.GetProperty("HasValue", AnyInstance);
        if (hasValueProp is not null && hasValueProp.PropertyType != typeof(bool)) hasValueProp = null;
        var hasValueField = hasValueProp is null ? t.GetField("HasValue", AnyInstance) : null;
        if (hasValueField is not null && hasValueField.FieldType != typeof(bool)) hasValueField = null;
        var valueProp = t.GetProperty("Value", AnyInstance) ?? t.GetProperty("value", AnyInstance);
        var valueField = valueProp is null ? (t.GetField("Value", AnyInstance) ?? t.GetField("value", AnyInstance)) : null;

        var handles = new NullableWrapperHandles(hasValueProp, hasValueField, valueProp, valueField);
        _nullableWrapperHandlesByType[t] = handles;
        DiagNullableShapeDiscovered(t, handles);
        return handles;
    }

    // Point 2 fix (sea/WwLG5Bq4ni): resolved + cached PER Type, never latched globally — the old
    // single `_bloodFieldsResolved` bool meant a first call that happened to see the WRAPPER type (no
    // BloodPercent/Stage members — those live on the unwrapped value type) permanently recorded
    // "unreadable" for every type forever after. A per-Type miss here IS the "recorded as unreadable
    // for this type" outcome the fix calls for — it's a legitimate memoization (a CLR Type's member
    // layout never changes at runtime), not a latch bug, because it can never apply to the WRONG type.
    // internal for direct unit coverage with plain C# test doubles.
    internal BloodFieldHandles ResolveBloodFields(Type t)
    {
        if (_bloodFieldHandlesByType.TryGetValue(t, out var cached)) return cached;

        var percentField = t.GetField("BloodPercent", AnyInstance);
        var percentProp = percentField is null ? t.GetProperty("BloodPercent", AnyInstance) : null;
        var stageField = t.GetField("Stage", AnyInstance);
        var stageProp = stageField is null ? t.GetProperty("Stage", AnyInstance) : null;

        var handles = new BloodFieldHandles(percentField, percentProp, stageField, stageProp);
        _bloodFieldHandlesByType[t] = handles;
        DiagBloodFieldsDiscovered(t, handles);
        return handles;
    }

    // Point 3 fix (sea/WwLG5Bq4ni): a null/unconvertible raw value now FAILS this tier (returns false)
    // instead of degrading to a reported 0% — see ToFloat's doc for what "unconvertible" means.
    private bool TryExtractPercent(long uuid, object result, BloodFieldHandles handles, out int percent)
    {
        percent = 0;
        try
        {
            var raw = handles.PercentField is not null ? handles.PercentField.GetValue(result) : handles.PercentProperty?.GetValue(result);
            var f = ToFloat(raw);
            // M4 review fix: the ≤1 "treat as fraction" heuristic below collapses a REAL 1% (the
            // scripted-kill floor value) down to 100% if it's actually already a percent — log the raw
            // value once per entity so the acceptance raid settles which scale BloodPercent uses.
            DiagRawBloodPercent(uuid, f);
            if (f is null) return false;
            percent = ToPercentInt(f.Value);
            return true;
        }
        catch { return false; }
    }

    // Stage is NOT the field that masked the wire tiers (BloodPercent is) — a missing/unconvertible
    // Stage degrades to 0 without failing the whole read, unlike TryExtractPercent above.
    private int ExtractStage(object result, BloodFieldHandles handles)
    {
        try
        {
            var raw = handles.StageField is not null ? handles.StageField.GetValue(result) : handles.StageProperty?.GetValue(result);
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

    // Point 4 fix (sea/WwLG5Bq4ni): returns null — NOT 0f — for a raw value that isn't one of the CLR
    // primitive numeric types this method actually knows how to read. The OLD version defaulted
    // unconvertible input (incl. a boxed Il2Cpp value this reflection layer can't unbox, or the
    // WRAPPER object itself if TryUnwrap somehow didn't run) to 0f, which is indistinguishable from a
    // genuine 0.0 reading — exactly the ambiguity that let ExtractPercent report a false 0% every tick.
    internal static float? ToFloat(object? v) => v switch
    {
        float f => f,
        double d => (float)d,
        int i => i,
        long l => l,
        _ => null,
    };
}
