using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;
namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// Cached VolumeParameter plumbing (Photo Studio fw fix round, perf review): every reflection lookup is resolved
/// once per (Type, name) and every parameter object once per (component, field), so a slider drag or the per-tick
/// focus write re-runs no <c>GetProperty</c> / <c>GetMethods</c> walk. The caches live as long as the components
/// they describe — <see cref="DestroyVolumeObjects"/> clears the per-parameter ones.
/// </summary>
internal sealed partial class ZRenderLookBackend
{
    private static readonly object[] FalseArg = { false };
    private readonly Dictionary<(Type, string), PropertyInfo?> _props = new();
    private readonly Dictionary<Type, MethodInfo> _setAllOverrides = new();
    // Null value = the field is missing on this build (warned once, never re-resolved).
    private readonly Dictionary<(string Component, string Field), ParamSlot?> _params = new();

    /// <summary>One resolved parameter: the wrapper object, its <c>value</c> / <c>overrideState</c> properties and,
    /// where the types allow, direct setter delegates (no boxing, no reflection args array on the focus path).</summary>
    private sealed class ParamSlot
    {
        public object Param = null!;
        public PropertyInfo Value = null!;
        public PropertyInfo Override = null!;
        public Action<float>? SetFloat;
        public Action<bool>? SetOverrideState;

        public void MarkOverridden()
        {
            if (SetOverrideState is not null) SetOverrideState(true);
            else Override.SetValue(Param, true);
        }
    }

    private PropertyInfo? FindProp(Type t, string name)
    {
        if (_props.TryGetValue((t, name), out var p)) return p;
        p = StellarInterop.FindPropertyUp(t, name);
        _props[(t, name)] = p;
        return p;
    }

    /// <summary>Drops every override on one component we added, so its group reverts to the game's value.</summary>
    private void ClearOverrides(string component)
    {
        if (!_components.TryGetValue(component, out var comp)) return;
        var type = comp.GetType();
        if (!_setAllOverrides.TryGetValue(type, out var m))
        {
            m = StellarInterop.FindMethod(type, "SetAllOverridesTo", 1)
                ?? throw new InvalidOperationException("VolumeComponent.SetAllOverridesTo not found");
            _setAllOverrides[type] = m;
        }
        m.Invoke(comp, FalseArg);
    }

    private void ClearAllOverrides()
    {
        foreach (var name in _components.Keys) ClearOverrides(name);
    }

    private ParamSlot? GetSlot(string component, string field)
    {
        if (_params.TryGetValue((component, field), out var slot)) return slot;
        var comp = GetOrAddComponent(component);
        var param = FindProp(comp.GetType(), field)?.GetValue(comp);
        if (param is null)
        {
            WarnOnce(component + "." + field, $"Photo look setting {component}.{field} not found; skipped.");
            _params[(component, field)] = null;
            return null;
        }
        var pt = param.GetType();
        slot = new ParamSlot
        {
            Param = param,
            Value = FindProp(pt, "value") ?? throw new InvalidOperationException($"{field}.value not found"),
            Override = FindProp(pt, "overrideState") ?? throw new InvalidOperationException($"{field}.overrideState not found"),
        };
        slot.SetFloat = SetterDelegate<float>(slot.Value, param);
        slot.SetOverrideState = SetterDelegate<bool>(slot.Override, param);
        _params[(component, field)] = slot;
        return slot;
    }

    // A closed setter delegate when the property is exactly T; null (→ reflection SetValue) otherwise.
    private static Action<T>? SetterDelegate<T>(PropertyInfo p, object target)
    {
        if (p.PropertyType != typeof(T) || p.GetSetMethod(nonPublic: true) is not { } setter) return null;
        try { return Delegate.CreateDelegate(typeof(Action<T>), target, setter, throwOnBindFailure: false) as Action<T>; }
        catch { return null; }
    }

    private void WriteParam(ParamWrite w)
    {
        if (GetSlot(w.Component, w.Field) is not { } slot) return;
        var value = ToUnity(w.Value, slot.Value.PropertyType);
        if (value is null) return;   // e.g. an unreadable LUT — already warned, leave the game's value
        slot.Value.SetValue(slot.Param, value);
        slot.MarkOverridden();
        OnParamWritten(w, value);
    }

    /// <summary>The focus-tracking write: cached slot + direct setter delegates, no per-call allocation.</summary>
    private void WriteFocus(float distance)
    {
        if (GetSlot(LookParameterPlan.DofComponent, LookParameterPlan.FocusField) is not { } slot) return;
        if (slot.SetFloat is not null) slot.SetFloat(distance);
        else slot.Value.SetValue(slot.Param, distance);
        slot.MarkOverridden();
        OnFocusWritten(distance);
    }
}
