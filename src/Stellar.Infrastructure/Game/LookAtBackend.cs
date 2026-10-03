using System;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game.Posing;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>
/// Look-at-camera on the local player with the game's photo-mode recipe (recon 6b/F): snapshot → <c>SetLookAtIKParam(m,1)</c>
/// + <c>SetLuaAttrLookAtHeadClose(false)</c> + <c>SetLookAtTransform(m, camera)</c>; restore = <c>ResetLookAtIKParam</c> +
/// <c>HeadClose(true)</c> + <c>SetLookAtTransform(null)</c>, then <see cref="LookAtRestorePlan"/>'s corrective writes. The
/// restore is skipped when the model was rebuilt (zone change) — the new model never carried our state. The snapshot read
/// itself (<c>AnimLookAtComp</c> lookEnable/headLookEnable/eyeLookEnable) is <see cref="LookAtSnapshotReader"/>'s — kept in
/// one place rather than duplicated here (controller review, posing Task 4 round 1). Main thread.
/// </summary>
internal sealed partial class LookAtBackend : ILookAtBackend
{
    internal const string HelperType = "Panda.ZGame.ZModelHelper";
    internal const string LookCompType = "Panda.ZGame.AnimLookAtCompBase";

    private readonly IGameTypeRegistry _types;
    private readonly GameEntityAccess _entities;
    private readonly Func<Camera?> _mainCamera;
    private readonly IPluginLog _log;
    private readonly LookAtSnapshotReader _snapshotReader;
    private MethodInfo? _setIk, _resetIk, _setTransform, _headClose, _eyeOpen, _enable;
    private LookAtSnapshot? _pre;
    private IntPtr _model;

    public LookAtBackend(IGameTypeRegistry types, GameEntityAccess entities, Func<Camera?> mainCamera, IPluginLog log)
    {
        _types = types;
        _entities = entities;
        _mainCamera = mainCamera;
        _log = log;
        _snapshotReader = new LookAtSnapshotReader(types);
    }

    public bool TryApply()
    {
        if (_pre is not null) return true;
        var model = _entities.LiveModel(_entities.LocalEntity());
        var cam = _mainCamera();
        if (!Resolve() || model is null || cam == null || _snapshotReader.Read(model) is not { } pre) return false;
        try
        {
            _setIk!.Invoke(null, new object[] { model, 1 });
            _headClose!.Invoke(model, new object[] { false });
            _setTransform!.Invoke(null, new object?[] { model, cam.transform, false, true });
            _pre = pre;
            _model = Pointer(model);
            OnApplied(pre);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning("[FreeCam] look-at failed: " + (ex.InnerException ?? ex).Message);
            return false;
        }
    }

    public void Restore()
    {
        if (_pre is not { } pre) return;
        _pre = null;
        var model = _entities.LiveModel(_entities.LocalEntity());
        if (model is null || Pointer(model) != _model) return;
        try
        {
            _resetIk!.Invoke(null, new[] { model });
            _headClose!.Invoke(model, new object[] { true });
            _setTransform!.Invoke(null, new object?[] { model, null, false, true });
            // Head never comes from this same-frame read (AfterRelease forces it to the recipe's own written value) —
            // see LookAtRestorePlan.AfterRelease.
            var after = LookAtRestorePlan.AfterRelease(_snapshotReader.Read(model));
            var writes = LookAtRestorePlan.Corrections(pre, after);
            foreach (var w in writes) Write(model, w);
            OnRestored(writes.Count);
        }
        catch (Exception ex)
        {
            _log.Warning("[FreeCam] look-at restore failed: " + (ex.InnerException ?? ex).Message);
        }
    }

    private void Write(object model, LookAtWrite w)
    {
        var method = w.Kind switch { LookAtWriteKind.HeadClose => _headClose, LookAtWriteKind.EyeOpen => _eyeOpen, _ => _enable };
        method!.Invoke(model, new object[] { w.Value });
    }

    private static IntPtr Pointer(object model) => (model as Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;

    private bool Resolve()
    {
        if (_enable is not null) return true;
        var helper = _types.FindType(HelperType);
        var model = _types.FindType(GameEntityAccess.ModelType);
        if (helper is null || model is null) return false;
        const BindingFlags S = BindingFlags.Public | BindingFlags.Static;
        _setIk = helper.GetMethods(S).FirstOrDefault(m => m.Name == "SetLookAtIKParam" && m.GetParameters() is { Length: 2 } p && p[1].ParameterType == typeof(int));
        _resetIk = helper.GetMethods(S).FirstOrDefault(m => m.Name == "ResetLookAtIKParam" && m.GetParameters().Length == 1);
        _setTransform = helper.GetMethods(S).FirstOrDefault(m => m.Name == "SetLookAtTransform" && m.GetParameters().Length == 4);
        _headClose = StellarInterop.FindMethod(model, "SetLuaAttrLookAtHeadClose", 1);
        _eyeOpen = StellarInterop.FindMethod(model, "SetLuaAttrLookAtEyeOpen", 1);
        var enable = StellarInterop.FindMethod(model, "SetLuaAttrLookAtEnable", 1);
        if (_setIk is null || _resetIk is null || _setTransform is null || _headClose is null || _eyeOpen is null || enable is null) return false;
        _enable = enable;
        return true;
    }

    partial void OnApplied(LookAtSnapshot pre);
    partial void OnRestored(int corrections);
}
