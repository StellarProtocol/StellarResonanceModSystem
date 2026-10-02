using System;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using UnityEngine;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// Calls on one <c>Panda.ZGame.ZModel</c> (recon § 1 table, runs 4–5): the held expression
/// (<c>SetLuaAttrEmoteInfo(face, -1, true, …)</c>), action readback, facing and place
/// (<c>EntityAttrExtensions.Get/SetAttrGoRotation</c>, <c>SetAttrGoPosition(…, WriteNotify.Now)</c>), every show layer on,
/// and the look calls on <c>ZModelHelper</c>. Overloads that share a parameter count (the ZModel vs ZEntity extension
/// pairs, the three <c>SetLookAtIKParam</c>) resolve by exact parameter types. Every public call resolves first; game
/// exceptions propagate to the pose model's boundary. Main thread.
/// </summary>
internal sealed partial class PoseModelCalls
{
    // ZModelHelper's type name is LookAtBackend.HelperType (one shared constant — review round 1).
    internal const string WriteNotifyType = "Panda.ZGame.Pure.WriteNotify";
    internal const string ShowLoadType = "Panda.ZGame.EShowLoadType";
    private const BindingFlags S = BindingFlags.Public | BindingFlags.Static;

    private readonly IGameTypeRegistry _types;
    private MethodInfo? _holdFace, _total, _passed, _head, _headClose, _eyeOpen, _showLoad;
    private MethodInfo? _getRot, _setRot, _getPos, _setPos, _ikPhoto, _ikReset, _lookTransform, _lookPos, _toLocal;
    private PropertyInfo? _destroying;
    private Type? _modelType;
    private object? _notifyNow;
    private object[]? _showAllArgs;

    public PoseModelCalls(IGameTypeRegistry types) => _types = types;

    /// <summary>Alive: not collected and not <c>IsDestroying</c> (docs/il2cpp-probing-safety.md).</summary>
    public bool IsLive(object? m) =>
        m is Il2CppObjectBase { WasCollected: false } && Resolve() && _destroying!.GetValue(m) is not true;

    public void HoldFace(object m, int faceId) { if (Resolve()) _holdFace!.Invoke(m, new object[] { faceId, -1f, true, false, 0f, false, false }); }
    public void ClearFace(object m) { if (Resolve()) _holdFace!.Invoke(m, new object[] { 0, 0f, false, false, 0f, false, false }); }
    public float TotalTime(object m) => Resolve() ? Convert.ToSingle(_total!.Invoke(m, null)) : 0f;
    public Vector3? Head(object m) => Resolve() && _head!.Invoke(m, null) is Vector3 v ? v : null;
    public void HeadClose(object m, bool closed) { if (Resolve()) _headClose!.Invoke(m, new object[] { closed }); }
    public void EyeOpen(object m, bool open) { if (Resolve()) _eyeOpen!.Invoke(m, new object[] { open }); }
    public Quaternion? Rotation(object m) => Resolve() && _getRot!.Invoke(null, new[] { m }) is Quaternion q ? q : null;
    public float Yaw(object m) => Rotation(m) is Quaternion q ? q.eulerAngles.y : 0f;
    public Vector3? Position(object m) => Resolve() && _getPos!.Invoke(null, new[] { m }) is Vector3 v ? v : null;
    public void ShowAll(object m) { if (Resolve()) _showLoad!.Invoke(m, _showAllArgs); }

    public void SetYaw(object m, float yaw)
    {
        if (Resolve()) _setRot!.Invoke(null, new object[] { m, Quaternion.Euler(0f, yaw, 0f) });
    }

    /// <summary>The generated model's pre-create placement (run 5 (2)): position with an immediate write, then rotation.</summary>
    public void Place(object m, Vector3 position, Quaternion rotation)
    {
        if (!Resolve()) return;
        _setPos!.Invoke(null, new[] { m, position, _notifyNow! });
        _setRot!.Invoke(null, new object[] { m, rotation });
    }

    public void IkPhoto(object m) { if (Resolve()) _ikPhoto!.Invoke(null, new object[] { m, 1 }); }
    public void IkReset(object m) { if (Resolve()) _ikReset!.Invoke(null, new[] { m }); }
    public void LookTransform(object m, Transform? t, bool main) { if (Resolve()) _lookTransform!.Invoke(null, new object?[] { m, t, false, main }); }
    public void LookPos(object m, Vector3 local, bool main) { if (Resolve()) _lookPos!.Invoke(null, new object[] { m, local, main }); }
    public Vector3? ToLocal(object m, Vector3 world) => Resolve() && _toLocal!.Invoke(null, new object[] { m, world }) is Vector3 v ? v : null;

    private bool Resolve()
    {
        if (_toLocal is not null) return true;
        var model = _types.FindType(GameEntityAccess.ModelType);
        var ext = _types.FindType(GameEntityAccess.AttrExtType);
        var helper = _types.FindType(LookAtBackend.HelperType);
        var notify = _types.FindType(WriteNotifyType);
        var show = _types.FindType(ShowLoadType);
        if (model is null || ext is null || helper is null || notify is null || show is null) return false;
        if (!ResolveModel(model) || !ResolveStatics(model, ext, helper, notify)) return false;
        var none = Enum.Parse(show, "ENone");
        _showAllArgs = new[] { none, none, none, Enum.Parse(show, "EAll") };
        _notifyNow = Enum.Parse(notify, "Now");
        _toLocal = helper.GetMethod("LuaWorldPosToLocal", S, null, new[] { model, typeof(Vector3) }, null);   // sentinel last
        return _toLocal is not null;
    }

    private bool ResolveModel(Type model)
    {
        _modelType = model;
        _holdFace = StellarInterop.FindMethod(model, "SetLuaAttrEmoteInfo", 7);
        _total = StellarInterop.FindMethod(model, "GetLuaAttrActionInfoTotalTime", 0);
        _passed = StellarInterop.FindMethod(model, "GetLuaAttrActionInfoPassedTime", 0);
        _head = StellarInterop.FindMethod(model, "GetHeadPosition", 0);
        _headClose = StellarInterop.FindMethod(model, "SetLuaAttrLookAtHeadClose", 1);
        _eyeOpen = StellarInterop.FindMethod(model, "SetLuaAttrLookAtEyeOpen", 1);
        _showLoad = StellarInterop.FindMethod(model, "SetLuaAttrModelShowLoadType", 4);
        _destroying = StellarInterop.FindPropertyUp(model, "IsDestroying");
        return _holdFace is not null && _total is not null && _passed is not null && _head is not null && _headClose is not null &&
               _eyeOpen is not null && _showLoad is not null && _destroying is not null;
    }

    private bool ResolveStatics(Type model, Type ext, Type helper, Type notify)
    {
        _getRot = ext.GetMethod("GetAttrGoRotation", S, null, new[] { model }, null);
        _setRot = ext.GetMethod("SetAttrGoRotation", S, null, new[] { model, typeof(Quaternion).MakeByRefType() }, null);
        _getPos = ext.GetMethod("GetAttrGoPosition", S, null, new[] { model }, null);
        _setPos = ext.GetMethod("SetAttrGoPosition", S, null, new[] { model, typeof(Vector3).MakeByRefType(), notify }, null);
        _ikPhoto = helper.GetMethod("SetLookAtIKParam", S, null, new[] { model, typeof(int) }, null);
        _ikReset = helper.GetMethod("ResetLookAtIKParam", S, null, new[] { model }, null);
        _lookTransform = helper.GetMethod("SetLookAtTransform", S, null, new[] { model, typeof(Transform), typeof(bool), typeof(bool) }, null);
        _lookPos = helper.GetMethod("SetLookAtPos", S, null, new[] { model, typeof(Vector3), typeof(bool) }, null);
        return _getRot is not null && _setRot is not null && _getPos is not null && _setPos is not null && _ikPhoto is not null &&
               _ikReset is not null && _lookTransform is not null && _lookPos is not null;
    }
}
