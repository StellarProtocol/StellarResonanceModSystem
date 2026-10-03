using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using UnityEngine;
namespace Stellar.Infrastructure.Game.Lights;

/// <summary>
/// The per-model calls of recon Run 13 Q3, resolved once: <c>ZModel.RenderComp</c> → <c>RenderCompBase</c>,
/// <c>SetFixedLight(ref ModelFixedLightData{LightParms}, 0x3FFFFFFF)</c> (writes <c>_CameraLightParm</c> on every material of
/// the model), <c>SetFresnelEffect(float, Color, Vector4, EModelRenderMask.All)</c> (<c>_UseFresnel</c> /
/// <c>_FresnelColor</c> / <c>_FresnelParms</c>) and the material list (<c>Renderers</c> → <c>getMatCount</c> /
/// <c>getMat</c> for the Render and Origin sets). <c>ModelFixedLightData</c> is passed BY REF: it goes through a plain
/// reflected <c>MethodInfo.Invoke</c> with a boxed struct — never a HarmonyX patch (docs/il2cpp-probing-safety.md § a
/// third failure class). Main thread.
/// </summary>
internal sealed class LightModelCalls
{
    private const string RenderCompType = "Panda.ZGame.RenderCompBase";
    private const string MatBaseType = "Panda.ZGame.ZModelRenderMatBase";
    private const string FixedLightType = "Panda.ZGame.ModelFixedLightData";
    private const string MaskType = "Panda.ZGame.EModelRenderMask";
    private const uint AllParts = 0x3FFFFFFF;

    private readonly IGameTypeRegistry _types;
    private PropertyInfo? _renderComp, _renderers, _destroying;
    private MethodInfo? _setFixed, _setFresnel, _matCount, _getMat;
    private FieldInfo? _lightParms;
    private Type? _fixedType;
    private object? _maskAll;
    private object[]? _targets;

    public LightModelCalls(IGameTypeRegistry types) => _types = types;

    /// <summary>Not collected and not <c>IsDestroying</c> (docs/il2cpp-probing-safety.md).</summary>
    public bool IsLive(object? model)
    {
        if (model is not Il2CppObjectBase { WasCollected: false }) return false;
        _destroying ??= StellarInterop.FindPropertyUp(model.GetType(), "IsDestroying");
        return _destroying is not null && _destroying.GetValue(model) is not true;
    }

    /// <summary>The model's render component, or null.</summary>
    public object? RenderComp(object model)
    {
        if (!Resolve()) return null;
        _renderComp ??= StellarInterop.FindPropertyUp(model.GetType(), "RenderComp");
        return _renderComp?.GetValue(model);
    }

    public void SetFixedLight(object renderComp, Vector4 parms)
    {
        if (!Resolve()) return;
        var data = Activator.CreateInstance(_fixedType!)!;
        _lightParms!.SetValue(data, parms);   // the boxed copy is what the by-ref argument receives
        _setFixed!.Invoke(renderComp, new object[] { data, AllParts });
    }

    public void SetFresnel(object renderComp, Color color, Vector4 parms)
    {
        if (Resolve()) _setFresnel!.Invoke(renderComp, new object[] { 1f, color, parms, _maskAll! });
    }

    /// <summary>Every material of the model once (Render then Origin set of each renderer, de-duplicated).</summary>
    public List<Material> Materials(object renderComp)
    {
        var list = new List<Material>();
        if (!Resolve()) return list;
        var seen = new HashSet<IntPtr>();
        foreach (var r in StellarInterop.Enumerate(_renderers!.GetValue(renderComp)))
        {
            if (r is null) continue;
            foreach (var target in _targets!)
            {
                var n = Convert.ToInt32(_matCount!.Invoke(r, new[] { target }));
                for (var k = 0; k < n; k++)
                    if (_getMat!.Invoke(r, new object[] { target, k }) is Material m && m != null && seen.Add(m.Pointer)) list.Add(m);
            }
        }
        return list;
    }

    private bool Resolve()
    {
        if (_targets is not null) return true;
        var rc = _types.FindType(RenderCompType);
        var mat = _types.FindType(MatBaseType);
        var fixedType = _types.FindType(FixedLightType);
        var mask = _types.FindType(MaskType);
        var targetType = mat?.GetNestedType("EMatSetTarget");
        if (rc is null || mat is null || fixedType is null || mask is null || targetType is null) return false;
        _setFixed = FindByRefFirst(rc, "SetFixedLight", fixedType);
        _setFresnel = rc.GetMethod("SetFresnelEffect", new[] { typeof(float), typeof(Color), typeof(Vector4), mask });
        _renderers = StellarInterop.FindPropertyUp(rc, "Renderers");
        _matCount = mat.GetMethod("getMatCount", new[] { targetType });
        _getMat = mat.GetMethod("getMat", new[] { targetType, typeof(int) });
        _lightParms = fixedType.GetField("LightParms");
        if (_setFixed is null || _setFresnel is null || _renderers is null || _matCount is null || _getMat is null || _lightParms is null)
            return false;
        _fixedType = fixedType;
        _maskAll = Enum.Parse(mask, "All");
        _targets = new[] { Enum.Parse(targetType, "Render"), Enum.Parse(targetType, "Origin") };
        return true;
    }

    // SetFixedLight([In] ref ModelFixedLightData, uint) — the only overload; matched on the by-ref element type.
    private static MethodInfo? FindByRefFirst(Type t, string name, Type element)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != name) continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType.IsByRef && ps[0].ParameterType.GetElementType() == element) return m;
        }
        return null;
    }
}
