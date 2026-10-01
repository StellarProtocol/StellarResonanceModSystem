using System;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>The per-frame read (the orbit centre on a posed copy — controller decision Q5) and the freeze stage that
/// works on a bare model (recon run 2 A / run 3 R3-1..R3-4 stage 2: <c>ZModel.AnimComp.Speed</c>; stage 1's attr factor
/// needs an entity, which a copy or generated model is not), through compiled accessors — no boxing, no
/// <c>MethodInfo.Invoke</c> per frame (<see cref="FastAccess"/>, as the camera cap's player read). Main thread.</summary>
internal sealed partial class PoseModelCalls
{
    private Func<object, bool>? _fastGone;
    private Func<object, Vector3>? _fastPos;
    private Func<object, object?>? _animComp;
    private Func<object, float>? _getSpeed;
    private Action<object, float>? _setSpeed;
    private bool _fastTried;

    /// <summary>The model's world position (<c>GetAttrGoPosition</c>), or null when it is gone or unreadable.</summary>
    public Position3D? FastPosition(object m)
    {
        if (!ResolveFast() || m is not Il2CppObjectBase { WasCollected: false } || _fastGone!(m)) return null;
        var v = _fastPos!(m);
        return new Position3D(v.x, v.y, v.z);
    }

    /// <summary>The drawn animation speed (<c>AnimComp.Speed</c>), or null when unreadable.</summary>
    public float? DrawnSpeed(object m) => ResolveFast() && _animComp!(m) is { } comp ? _getSpeed!(comp) : null;

    public void SetDrawnSpeed(object m, float speed)
    {
        if (ResolveFast() && _animComp!(m) is { } comp) _setSpeed!(comp, speed);
    }

    private bool ResolveFast()
    {
        if (_fastTried) return _setSpeed is not null;
        if (!Resolve()) return false;   // the slow handles first: the game types must be loaded
        _fastTried = true;
        _fastGone = FastAccess.Getter<bool>(_destroying);
        _fastPos = FastAccess.StaticFunc1<object, Vector3>(_getPos);
        var compProp = StellarInterop.FindPropertyUp(_modelType, "AnimComp");
        var speed = compProp is null ? null : StellarInterop.FindPropertyUp(compProp.PropertyType, "Speed");
        _animComp = FastAccess.Getter<object?>(compProp);
        _getSpeed = FastAccess.Getter<float>(speed);
        var set = FastAccess.Setter<float>(speed);
        if (_fastGone is null || _fastPos is null || _animComp is null || _getSpeed is null || set is null) return false;
        _setSpeed = set;   // set last: the "fast path available" sentinel
        return true;
    }
}
