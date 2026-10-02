using System;
using Stellar.Abstractions.Services;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Application.Abstractions;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>One combat-freeze sample of one monster / boss (diagnostics only). NaN / −1 / null = unreadable.</summary>
internal sealed class FreezeDiagReading
{
    public int EntId { get; set; } = -1;
    public bool Boss { get; set; }
    public string EntClass { get; set; } = "?";
    public IntPtr Model { get; set; }
    public IntPtr Comp { get; set; }
    public IntPtr Controller { get; set; }
    public IntPtr Go { get; set; }
    public string CompClass { get; set; } = "?";
    public string ControllerClass { get; set; } = "?";
    public float Drawn { get; set; } = float.NaN;
    public float ControllerSpeed { get; set; } = float.NaN;
    public float Factor { get; set; } = float.NaN;
    public float AnimSpeed { get; set; } = float.NaN;
    public int Skill { get; set; } = -1;
    public int SkillEffect { get; set; } = -1;
    public int Action { get; set; } = -1;
    public int State { get; set; } = -1;
    public Vector3? Logic { get; set; }
    public Vector3? DrawnPos { get; set; }
}

/// <summary>Reflected reads behind the combat-freeze sampler (diagnostics only; owner report 2026-10-02). Reads one LIVE
/// entity the caller just fetched through <c>ZEntityMgr.GetEntity</c> (null once culled) and gated on <c>IsDestroying</c>
/// for both entity and model (docs/il2cpp-probing-safety.md § 1, § 3); nothing read here is cached across frames except
/// member handles, and every read has its own try, so an entity that despawns mid-read yields "unreadable", never a stop.
/// A read that has thrown <see cref="MaxFailures"/> times without one success is switched off (no exception storm).
/// Beyond the sampler's own drawn speed it reads the anim CONTROLLER behind it (<c>AnimCompBase.animController_</c> →
/// <c>IAnimController.Speed</c>: <c>ECSAnimController.set_Speed</c> has 6 native callers that bypass the gated
/// <c>AnimCompBase.set_Speed</c>). Main thread.</summary>
internal sealed class FreezeDiagReader
{
    private const int MaxFailures = 5;
    private enum R { Factor, AnimSpeed, Skill, SkillEffect, Action, State, Controller, CtlSpeed, EntId, Boss, Count }

    private readonly IGameTypeRegistry _types;
    private readonly GameEntityAccess _entities;
    private readonly object[] _arg = new object[1];
    private readonly int[] _fail = new int[(int)R.Count];
    private readonly bool[] _ok = new bool[(int)R.Count];
    private MethodInfo? _factor, _animSpeed, _skill, _skillFx, _action, _state;
    private PropertyInfo? _entId, _boss, _animComp, _ctl, _ctlSpeed, _compSpeed, _goComp, _goPos;
    private bool _resolved;

    public FreezeDiagReader(IGameTypeRegistry types, GameEntityAccess entities)
    {
        _types = types;
        _entities = entities;
    }

    /// <summary>Reads <paramref name="entity"/> (live, just fetched) into <paramref name="into"/>.</summary>
    public void Read(object entity, FreezeDiagReading into)
    {
        Resolve();
        into.EntClass = ClassName(entity);
        into.EntId = Convert.ToInt32(Get(R.EntId, _entId, entity) ?? -1);
        into.Boss = Get(R.Boss, _boss, entity) is true;
        into.Factor = Float(Call(R.Factor, _factor, entity));
        into.AnimSpeed = Float(Call(R.AnimSpeed, _animSpeed, entity));
        into.Skill = Int(Call(R.Skill, _skill, entity));
        into.SkillEffect = Int(Call(R.SkillEffect, _skillFx, entity));
        into.State = Int(Call(R.State, _state, entity));
        if (_entities.LiveModel(entity) is not { } model) return;
        into.Model = Ptr(model);
        into.Action = Int(Call(R.Action, _action, model));
        into.Logic = _entities.AttrPosition(model);
        ReadAnim(model, into);
        ReadGo(model, into);
    }

    private void ReadAnim(object model, FreezeDiagReading into)
    {
        var comp = Safe(_animComp, model);
        if (comp is null) return;
        into.Comp = Ptr(comp);
        into.CompClass = ClassName(comp);
        into.Drawn = Float(Safe(_compSpeed, comp));
        var ctl = Get(R.Controller, _ctl, comp);
        if (ctl is null) return;
        into.Controller = Ptr(ctl);
        into.ControllerClass = ClassName(ctl);
        into.ControllerSpeed = Float(Get(R.CtlSpeed, _ctlSpeed, ctl));
    }

    private void ReadGo(object model, FreezeDiagReading into)
    {
        var go = Safe(_goComp, model);
        if (go is null) return;
        into.Go = Ptr(go);
        if (Safe(_goPos, go) is Vector3 v) into.DrawnPos = v;
    }

    private void Resolve()
    {
        if (_resolved) return;
        var ext = _types.FindType(GameEntityAccess.AttrExtType);
        var ent = _types.FindType(GameEntityAccess.EntityType);
        var model = _types.FindType(GameEntityAccess.ModelType);
        var anim = _types.FindType(DrawnSpeedPatch.AnimCompType);
        var ctl = _types.FindType("Panda.ZGame.IAnimController");
        var go = _types.FindType(GameFreezeBackend.ModelGoCompType);
        if (ext is null || ent is null || model is null) return;
        _factor = Static1(ext, "GetAttrSkillStageTimeFactor", null);
        _animSpeed = Static1(ext, "GetAttrAnimSpeed", ent);
        _skill = Static1(ext, "GetSkillId", null);
        _skillFx = Static1(ext, "GetSkillEffectId", null);
        _state = Static1(ext, "GetAttrState", ent);
        _action = Static1(ext, "GetAttrActionInfoActionId", model);
        _entId = StellarInterop.FindPropertyUp(ent, "EntId");
        _boss = StellarInterop.FindPropertyUp(ent, "IsBoss");
        _animComp = StellarInterop.FindPropertyUp(model, "AnimComp");
        _goComp = StellarInterop.FindPropertyUp(model, "ModelGoComp");
        _compSpeed = StellarInterop.FindPropertyUp(anim, "Speed");
        _ctl = StellarInterop.FindPropertyUp(anim, "animController_");
        _ctlSpeed = StellarInterop.FindPropertyUp(ctl, "Speed");
        _goPos = StellarInterop.FindPropertyUp(go, "Position");
        _resolved = true;
    }

    private static MethodInfo? Static1(Type ext, string name, Type? arg) =>
        (arg is null ? null : ext.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, new[] { arg }, null))
        ?? StellarInterop.FindMethod(ext, name, 1);

    private object? Call(R r, MethodInfo? m, object target)
    {
        if (m is null || Off(r)) return null;
        _arg[0] = target;
        try { var v = m.Invoke(null, _arg); _ok[(int)r] = true; return v; }
        catch { _fail[(int)r]++; return null; }
    }

    private object? Get(R r, PropertyInfo? p, object target)
    {
        if (p is null || Off(r)) return null;
        try { var v = p.GetValue(target); _ok[(int)r] = true; return v; }
        catch { _fail[(int)r]++; return null; }
    }

    private bool Off(R r) => !_ok[(int)r] && _fail[(int)r] >= MaxFailures;

    private static object? Safe(PropertyInfo? p, object target)
    {
        if (p is null) return null;
        try { return p.GetValue(target); }
        catch { return null; }
    }

    private static float Float(object? v)
    {
        try { return v is null ? float.NaN : Convert.ToSingle(v); }
        catch { return float.NaN; }
    }

    private static int Int(object? v)
    {
        try { return v is null ? -1 : Convert.ToInt32(v); }
        catch { return -1; }
    }

    internal static IntPtr Ptr(object? o)
    {
        try { return (o as Il2CppObjectBase)?.Pointer ?? IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    /// <summary>The object's concrete IL2CPP class name (il2cpp_object_get_class → il2cpp_class_get_name), or "?".</summary>
    internal static string ClassName(object? o)
    {
        try
        {
            var p = Ptr(o);
            if (p == IntPtr.Zero) return "?";
            var cls = IL2CPP.il2cpp_object_get_class(p);
            return cls == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls)) ?? "?";
        }
        catch { return "?"; }
    }
}
