using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// The photo panel's action / face / photo-copy calls on <c>Panda.ZAnim.ZAnimActionPlayMgr</c> (recon § 1 table, runs 4–5):
/// the no-model overloads for the local player, the <c>ZModel</c> overloads for a copy or a generated model, and
/// <c>syncServer</c> always false. Every overload pair differs in parameter count, so each resolves by name + count. The
/// singleton is read through <see cref="SingletonAccess"/> (never constructed). Handles resolve lazily and retry until all
/// are found. Exceptions propagate to the caller (the pose model's boundary logs them). Main thread.
/// </summary>
internal sealed class PoseActionCalls
{
    internal const string MgrType = "Panda.ZAnim.ZAnimActionPlayMgr";

    private readonly IGameTypeRegistry _types;
    private readonly SingletonAccess _mgr = new();
    private MethodInfo? _playSelf, _playModel, _persistSelf, _persistModel, _resetSelf, _resetModel;
    private MethodInfo? _faceSelf, _faceModel, _resetFaceSelf, _resetFaceModel, _clone, _recycle, _animInfo, _animTotal;

    public PoseActionCalls(IGameTypeRegistry types) => _types = types;

    // Mgr() is the first argument on purpose: C# evaluates arguments left to right, so the handle fields are read only
    // after Mgr() has resolved them.
    public bool PlaySelf(int id) => Call(Mgr(), _playSelf, id, false, 0f, -1f, false, 0f, false, true, 0, 0, false);
    public bool PlayModel(object m, int id) =>
        Call(Mgr(), _playModel, m, id, false, 0f, -1f, true, 0f, false, true, 0, 0, false, false, false);
    public bool PersistSelf(float time) => Call(Mgr(), _persistSelf, time);
    public bool PersistModel(object m, float time) => Call(Mgr(), _persistModel, m, time);
    public bool ResetSelf() => Call(Mgr(), _resetSelf);
    public bool ResetModel(object m) => Call(Mgr(), _resetModel, m, false);
    public bool FaceSelf(int faceId) => Call(Mgr(), _faceSelf, faceId, false);
    public bool FaceModel(object m, int faceId) => Call(Mgr(), _faceModel, m, faceId, false, 0f);
    public bool ResetFaceSelf() => Call(Mgr(), _resetFaceSelf);
    public bool ResetFaceModel(object m) => Call(Mgr(), _resetFaceModel, m);
    public bool Recycle(object clone) => Call(Mgr(), _recycle, clone);

    /// <summary><c>CloneModelForPhoto(entity)</c> — the game's photo copy of another player (null when unavailable).</summary>
    public object? Clone(object entity)
    {
        var mgr = Mgr();
        return mgr is null ? null : _clone!.Invoke(mgr, new[] { entity });
    }

    /// <summary>The action's length for <paramref name="gender"/> (1 male, 2 female) from the action table; 0 when unknown.</summary>
    public float AnimTotal(int actionId, int gender)
    {
        var mgr = Mgr();
        if (mgr is null || actionId <= 0) return 0f;
        var info = _animInfo!.Invoke(mgr, new object[] { actionId });
        return info is null ? 0f : Convert.ToSingle(_animTotal!.Invoke(info, new object[] { gender }));
    }

    private object? Mgr() => Resolve() ? _mgr.Get() : null;

    private static bool Call(object? mgr, MethodInfo? method, params object[] args)
    {
        if (mgr is null || method is null) return false;
        method.Invoke(mgr, args);
        return true;
    }

    private bool Resolve()
    {
        if (_animTotal is not null) return true;
        var t = _types.FindType(MgrType);
        if (t is null || !_mgr.Resolve(t)) return false;
        _playSelf = StellarInterop.FindMethod(t, "PlayAction", 11);
        _playModel = StellarInterop.FindMethod(t, "PlayAction", 14);
        _persistSelf = StellarInterop.FindMethod(t, "SetActionPersistTime", 1);
        _persistModel = StellarInterop.FindMethod(t, "SetActionPersistTime", 2);
        _resetSelf = StellarInterop.FindMethod(t, "ResetAction", 0);
        _resetModel = StellarInterop.FindMethod(t, "ResetAction", 2);
        _faceSelf = StellarInterop.FindMethod(t, "PlayEmote", 2);
        _faceModel = StellarInterop.FindMethod(t, "PlayEmote", 4);
        _resetFaceSelf = StellarInterop.FindMethod(t, "ResetEmote", 0);
        _resetFaceModel = StellarInterop.FindMethod(t, "ResetEmote", 1);
        _clone = StellarInterop.FindMethod(t, "CloneModelForPhoto", 1);
        _recycle = StellarInterop.FindMethod(t, "RecyclePhotoModel", 1);
        _animInfo = StellarInterop.FindMethod(t, "GetActionAnimInfoByActionId", 1);
        var total = _animInfo is null ? null : StellarInterop.FindMethod(_animInfo.ReturnType, "GetTotalTime", 1);
        if (_playSelf is null || _playModel is null || _persistSelf is null || _persistModel is null || _resetSelf is null ||
            _resetModel is null || _faceSelf is null || _faceModel is null || _resetFaceSelf is null || _resetFaceModel is null ||
            _clone is null || _recycle is null || total is null) return false;
        _animTotal = total;   // set last: the "fully resolved" sentinel
        return true;
    }
}
