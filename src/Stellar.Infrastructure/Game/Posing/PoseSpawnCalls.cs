using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using UnityEngine;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// The Partner path and the hide switch (recon run 5 (2), camera_member_vm GenerateModelAsync / camerasys_vm
/// OpenGroupPhoto): <c>LuaAsyncBridge.GenerateNormalModelAsyncByLua(modelId, pre, onLoad, onException, isCameraScene: true)</c>
/// with <c>ApplyModelBaseIdleByLua(scene, model)</c>, <c>ZModelManager.RecycleModelByLua(model)</c>,
/// <c>CameraFrameCtrl.SetTargetEntityVisible(entity, visible, ECommon)</c> (the panel passes the default source) and the
/// local player's rotation <c>LuaAsyncBridge.SetEntityRotation</c>. The managed callbacks are handed to IL2CPP through the
/// interop delegate's <c>op_Implicit</c> (Il2CppInterop <c>DelegateSupport</c>, as <c>MessagePipeContainerBridge</c>
/// does); the caller keeps them alive until the load ends. A callback body that throws never crosses back into IL2CPP,
/// but is reported once per exception type through the shared warn sink rather than swallowed (review round 1) — never
/// silent, never fatal. Main thread.
/// </summary>
internal sealed class PoseSpawnCalls
{
    internal const string BridgeType = "Panda.LuaAsyncBridge";
    internal const string FrameCtrlType = "Panda.ZGame.CameraFrameCtrl";
    internal const string ModelMgrType = "Panda.ZGame.ZModelManager";
    private static readonly MethodInfo ForwardMethod =
        typeof(PoseSpawnCalls).GetMethod(nameof(Forward), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly IGameTypeRegistry _types;
    private readonly Action<string> _warn;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly SingletonAccess _frameCtrl = new();
    private readonly SingletonAccess _modelMgr = new();
    private MethodInfo? _generate, _applyIdle, _rotateEntity, _recycle, _visible;
    private object? _visibleSource;

    public PoseSpawnCalls(IGameTypeRegistry types, Action<string> warn)
    {
        _types = types;
        _warn = warn;
    }

    public bool SetVisible(object entity, bool visible)
    {
        if (!Resolve() || _frameCtrl.Get() is not { } ctrl) return false;
        _visible!.Invoke(ctrl, new[] { entity, visible, _visibleSource! });
        return true;
    }

    public void RotateEntity(object entity, float yaw)
    {
        if (Resolve()) _rotateEntity!.Invoke(null, new object[] { entity, Quaternion.Euler(0f, yaw, 0f) });
    }

    public bool ApplyBaseIdle(object source, object target) =>
        Resolve() && _applyIdle!.Invoke(null, new[] { source, target }) is true;

    public bool RecycleModel(object model)
    {
        if (!Resolve() || _modelMgr.Get() is not { } mgr) return false;
        _recycle!.Invoke(mgr, new[] { model });
        return true;
    }

    /// <summary>Requests a generated model; false when the request could not be made (no callback will come). Keep
    /// <paramref name="keepAlive"/> referenced until <paramref name="onLoad"/> or <paramref name="onError"/> ran.</summary>
    public bool Generate(int modelId, Action<object> pre, Action<object> onLoad, Action<object> onError, out object[] keepAlive)
    {
        keepAlive = Array.Empty<object>();
        if (!Resolve()) return false;
        var p = _generate!.GetParameters();
        var managed = new[] { Managed(p[1].ParameterType, pre), Managed(p[2].ParameterType, onLoad), Managed(p[3].ParameterType, onError) };
        var native = new[] { ToIl2Cpp(managed[0], p[1].ParameterType), ToIl2Cpp(managed[1], p[2].ParameterType), ToIl2Cpp(managed[2], p[3].ParameterType) };
        if (native.Any(n => n is null)) return false;
        keepAlive = new object[] { managed[0], managed[1], managed[2], native[0]!, native[1]!, native[2]! };
        _generate.Invoke(null, new object?[] { modelId, native[0], native[1], native[2], true });
        return true;
    }

    // System.Action<T> for the interop delegate's T (ZModel / Il2CppSystem.Exception); never lets a managed exception
    // cross back into IL2CPP — but never swallows it silently either (review round 1: routed to WarnOnce below).
    private Delegate Managed(Type il2cppAction, Action<object> body) =>
        (Delegate)ForwardMethod.MakeGenericMethod(il2cppAction.GetGenericArguments()[0])
            .Invoke(null, new object[] { body, (Action<Exception>)OnCallbackThrew })!;

    private static Action<T> Forward<T>(Action<object> body, Action<Exception> onError) => value =>
    {
        try { body(value!); }
        catch (Exception ex) { onError(ex); }
    };

    private void OnCallbackThrew(Exception ex) =>
        WarnOnce("callback:" + ex.GetType().Name, $"posing generate callback threw: {ex.GetType().Name}: {ex.Message}");

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) _warn(message);
    }

    private static object? ToIl2Cpp(Delegate managed, Type il2cppType) =>
        il2cppType.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, null, new[] { managed.GetType() }, null)
            ?.Invoke(null, new object[] { managed });

    private bool Resolve()
    {
        if (_visible is not null) return true;
        var bridge = _types.FindType(BridgeType);
        var frame = _types.FindType(FrameCtrlType);
        var models = _types.FindType(ModelMgrType);
        if (bridge is null || frame is null || models is null || !_frameCtrl.Resolve(frame) || !_modelMgr.Resolve(models)) return false;
        _generate = bridge.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "GenerateNormalModelAsyncByLua" && m.GetParameters().Length == 5);
        _applyIdle = StellarInterop.FindMethod(bridge, "ApplyModelBaseIdleByLua", 2);
        _rotateEntity = StellarInterop.FindMethod(bridge, "SetEntityRotation", 2);
        _recycle = StellarInterop.FindMethod(models, "RecycleModelByLua", 1);
        var visible = StellarInterop.FindMethod(frame, "SetTargetEntityVisible", 3);
        if (_generate is null || _applyIdle is null || _rotateEntity is null || _recycle is null || visible is null) return false;
        var source = visible.GetParameters()[2];
        _visibleSource = source.HasDefaultValue && source.DefaultValue is not null
            ? Enum.ToObject(source.ParameterType, Convert.ToInt64(source.DefaultValue))
            : Enum.Parse(source.ParameterType, "ECommon");
        _visible = visible;   // set last: the "fully resolved" sentinel
        return true;
    }
}
