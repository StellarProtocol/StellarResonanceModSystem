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
    private MethodInfo? _getWorldLuaAttr;   // GetWorldLuaAttr(int) -> IAttr
    private PropertyInfo? _instanceProp;     // static ZSingleton<ZWorld>.Instance
    private PropertyInfo? _attrValueProp;    // IAttr.Value (resolved lazily off the first result)
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
            _attrValueProp ??= ResolveValueProp(attr);
            if (_attrValueProp is null) { _disabled = true; DiagAttrShape(attr); return; }
            var raw = _attrValueProp.GetValue(attr);
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

    // The Il2CppInterop IAttr wrapper's numeric value accessor isn't reliably a property literally
    // named "Value" on the concrete runtime type (the live probe proved GetProperty("Value") null).
    // Prefer a readable "Value" property; else the first readable integer-typed instance property.
    // (DiagAttrShape dumps the real member surface when this returns null, to nail it definitively.)
    private static PropertyInfo? ResolveValueProp(object attr)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        var t = attr.GetType();
        var named = t.GetProperty("Value", F);
        if (named is not null && named.CanRead && named.GetIndexParameters().Length == 0) return named;
        foreach (var p in t.GetProperties(F))
        {
            if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
            var pt = p.PropertyType;
            if (pt == typeof(int) || pt == typeof(long) || pt == typeof(uint) || pt == typeof(short)) return p;
        }
        return null;
    }
}
