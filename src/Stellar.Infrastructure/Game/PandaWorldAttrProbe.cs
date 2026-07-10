using System;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reads World-scoped attributes that ride the game's <c>ZWorld</c> singleton (NOT the wire) and
/// feeds them into the dungeon-state sink. Currently: <c>AttrDeathCount</c> (348) = the settlement
/// "Defeated" count, read via <c>Panda.ZGame.ZWorld.Instance.GetWorldLuaAttr(348).Value</c> —
/// exactly how the game's own dungeon UI reads it (lua <c>Z.World:GetWorldLuaAttr</c>).
///
/// <para>
/// Runs on the MAIN-THREAD framework tick ONLY (never the network receive thread — a live IL2CPP
/// read off-thread can fault). Reflection over the Il2CppInterop wrapper (Infrastructure holds no
/// typed <c>Panda.*</c> reference). Fully defensive: a missing type/method permanently disables the
/// probe; a transient-null Instance (pre-world) retries; any read exception disables + logs once.
/// Never throws across the IL2CPP boundary.
/// </para>
///
/// <para>Diagnostics live in <c>PandaWorldAttrProbe.Diagnostics.cs</c>.</para>
/// </summary>
internal sealed partial class PandaWorldAttrProbe
{
    private const string ZWorldTypeName = "Panda.ZGame.ZWorld";

    private readonly IDungeonStateSink _sink;
    private readonly IDungeonState _state;
    private readonly IPluginLog _log;

    private bool _disabled;                 // permanent: type/method missing or a read faulted
    private Type? _zworldType;
    private MethodInfo? _getWorldLuaAttr;   // GetWorldLuaAttr(int) -> Zproto.IAttr (abstract base)
    private PropertyInfo? _instanceProp;     // static ZSingleton<ZWorld>.Instance
    // The runtime object GetWorldLuaAttr returns is a concrete Zproto.ZAttr<int>, but Il2CppInterop
    // hands it back wrapped as the abstract Zproto.IAttr base (which has NO Value member — only
    // ParseProto/BindWatcher). So we re-wrap the returned pointer as ZAttr<int> and read its Value.
    private ConstructorInfo? _zattrIntCtor;  // Zproto.ZAttr<int>.ctor(IntPtr)
    private PropertyInfo? _zattrIntValue;    // Zproto.ZAttr<int>.Value  (int)
    private PropertyInfo? _pointerProp;      // Il2CppObjectBase.Pointer (off the returned attr)
    private int _lastDefeated = -1;

    public PandaWorldAttrProbe(IDungeonStateSink sink, IDungeonState state, IPluginLog log)
    {
        _sink  = sink  ?? throw new ArgumentNullException(nameof(sink));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _log   = log   ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Called from the throttled MAIN-THREAD framework tick. During an instanced run, reads the
    /// World <c>AttrDeathCount</c> (348) and latches it as the Defeated count. No-op in town, before
    /// the type/Instance resolve, or on any failure.
    /// </summary>
    public void Tick()
    {
        if (_disabled) return;
        if (_state.CurrentRunId == 0) return;   // only inside a dungeon/instanced run

        try
        {
            if (!TryResolveStatics()) return;                 // type + method (permanent-fail if missing)
            var instance = _instanceProp!.GetValue(null);     // ZSingleton Instance (may be null pre-world → retry)
            if (instance is null) return;

            var attr = _getWorldLuaAttr!.Invoke(instance, new object[] { AttrTypeIds.AttrDeathCount });
            if (attr is null) return;
            if (!TryResolveAttrInt(attr)) { DiagAttrShape(attr); return; }  // disables on hard-miss

            var ptr = (IntPtr)_pointerProp!.GetValue(attr)!;                // native ZAttr<int> pointer
            if (ptr == IntPtr.Zero) return;
            var typed = _zattrIntCtor!.Invoke(new object[] { ptr });        // re-wrap as ZAttr<int>
            var raw = _zattrIntValue!.GetValue(typed);
            if (raw is null) return;

            int value = Convert.ToInt32(raw);
            if (value <= 0 || value == _lastDefeated) return; // only latch a changed, positive count
            _lastDefeated = value;
            _sink.SetDefeated(value);
            DiagDefeated(value);
        }
        catch (Exception ex)
        {
            _disabled = true;   // stop retrying a broken path
            DiagFaulted(ex);
        }
    }

    // Resolve the ZWorld type + GetWorldLuaAttr(int) + static Instance property ONCE. A missing type
    // or method permanently disables the probe (there's no point retrying). Returns true once resolved.
    private bool TryResolveStatics()
    {
        if (_getWorldLuaAttr is not null && _instanceProp is not null) return true;

        _zworldType ??= AccessTools.TypeByName(ZWorldTypeName);
        if (_zworldType is null) { _disabled = true; DiagResolveMissing("type " + ZWorldTypeName); return false; }

        _getWorldLuaAttr ??= _zworldType.GetMethod("GetWorldLuaAttr", new[] { typeof(int) });
        _instanceProp ??= _zworldType.GetProperty(
            "Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

        if (_getWorldLuaAttr is null || _instanceProp is null)
        {
            _disabled = true;
            DiagResolveMissing(_getWorldLuaAttr is null ? "GetWorldLuaAttr(int)" : "static Instance");
            return false;
        }
        return true;
    }

    // Resolve, ONCE, the concrete Zproto.ZAttr<int> wrapper (ctor + Value getter) and the
    // Il2CppObjectBase.Pointer accessor off the returned attr. The value member is NOT on the
    // abstract Zproto.IAttr the method returns — it lives on the concrete generic instantiation
    // (confirmed offline: ilspycmd Panda.ZRpcGen Zproto.ZAttr`1 → `public T Value`). Returns false
    // (and permanently disables) if any piece is missing.
    private bool TryResolveAttrInt(object attr)
    {
        if (_zattrIntValue is not null && _zattrIntCtor is not null && _pointerProp is not null) return true;

        _pointerProp ??= attr.GetType().GetProperty(
            "Pointer", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

        var open = AccessTools.TypeByName("Zproto.ZAttr`1");
        var closed = open?.MakeGenericType(typeof(int));
        _zattrIntCtor ??= closed?.GetConstructor(new[] { typeof(IntPtr) });
        _zattrIntValue ??= closed?.GetProperty("Value");

        if (_pointerProp is null || _zattrIntCtor is null || _zattrIntValue is null)
        {
            _disabled = true;
            DiagResolveMissing(open is null ? "type Zproto.ZAttr`1"
                : _pointerProp is null ? "Il2CppObjectBase.Pointer" : "ZAttr<int>.ctor/Value");
            return false;
        }
        return true;
    }
}
