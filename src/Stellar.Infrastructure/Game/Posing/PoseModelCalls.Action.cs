using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>The action readback (recon docs/recon/photo-posing-recon.md § 2: <c>ZModel.GetLuaAttrActionInfoActionId /
/// TotalTime / PassedTime</c>, all <c>float</c> in release_3.7; local reads, 0 sends) through compiled accessors — the
/// Person panel polls it at ~10 Hz, so no <c>MethodInfo.Invoke</c> and no boxing per read. Liveness-gated exactly like
/// <see cref="FastPosition"/>. Main thread.</summary>
internal sealed partial class PoseModelCalls
{
    private Func<object, float>? _fastActionId, _fastTotal, _fastPassed;
    private bool _actionTried;

    /// <summary>What <paramref name="m"/> is doing and how far it has played (0 when its own length is unknown);
    /// <see cref="PoseActionReading.None"/> when idle, gone, destroying, or unreadable. Never throws.</summary>
    public PoseActionReading ReadAction(object? m)
    {
        if (m is null || !ResolveAction() || !IsLiveFast(m)) return PoseActionReading.None;
        try
        {
            var id = (int)MathF.Round(_fastActionId!(m));
            if (id <= 0) return PoseActionReading.None;
            var fraction = PoseMath.Fraction(_fastPassed!(m), _fastTotal!(m));
            return new PoseActionReading(id, fraction < 0f ? 0f : fraction);
        }
        catch { return PoseActionReading.None; }
    }

    /// <summary>The three raw readbacks, unclamped and including an id of 0 (diagnostics: the end-of-action probe). False
    /// when the model is gone or unreadable. Never throws.</summary>
    public bool ReadRaw(object? m, out float id, out float passed, out float total)
    {
        id = passed = total = 0f;
        if (m is null || !ResolveAction() || !IsLiveFast(m)) return false;
        try
        {
            id = _fastActionId!(m);
            passed = _fastPassed!(m);
            total = _fastTotal!(m);
            return true;
        }
        catch { return false; }
    }

    private bool ResolveAction()
    {
        if (_actionTried) return _fastPassed is not null;
        if (!ResolveFast()) return false;   // the liveness accessor + the slow handles first
        _actionTried = true;
        var id = FastAccess.Func0<float>(StellarInterop.FindMethod(_modelType!, "GetLuaAttrActionInfoActionId", 0));
        var total = FastAccess.Func0<float>(_total);
        var passed = FastAccess.Func0<float>(_passed);
        if (id is null || total is null || passed is null) return false;
        _fastActionId = id;
        _fastTotal = total;
        _fastPassed = passed;   // set last: the "action read available" sentinel
        return true;
    }
}
