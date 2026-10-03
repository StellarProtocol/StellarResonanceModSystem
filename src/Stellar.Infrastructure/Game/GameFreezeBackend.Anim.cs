using System;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>Holds the animation REQUESTS of the press's entities while the clock is stopped (the rule and its evidence:
/// <see cref="AnimRequestGate"/>). Typed run-original prefixes (<see cref="AnimGatePatch"/>) on the ECS animation controller's
/// four internal play methods (<c>Panda.ZGame.ECSAnimController.playBaseState</c> / <c>playUpperState</c> /
/// <c>playAdditiveState</c> / <c>playManualClip</c> — every public, sequence and state-end request funnels through them,
/// release_3.7 ISIL). Tracked at the press: each entity's live model's controller (never the local player or their own mount —
/// excluded from <c>_ids</c> — never a posing copy or NPC model, which have controllers of their own, and nobody at all when
/// the press does not know the local player). A leaving entity is untracked from the <c>RemoveEntity</c> prefix chain (it runs
/// only when the removal really happens). The kept requests are replayed on unfreeze, before the hold releases and the clock
/// runs, each only while its entity still serves the same controller (<see cref="AnimRequestGate.TakeReplay"/>). Installed on
/// the first freeze, for the session (never unpatched per freeze); kill switch <see cref="AnimGateEnvVar"/>=0. Main thread.</summary>
internal sealed partial class GameFreezeBackend
{
    internal const string AnimControllerType = "Panda.ZGame.ECSAnimController";
    internal const string EcsAnimCompType = "Panda.ZGame.ECSAnimComp";
    internal const string AnimGateEnvVar = "STELLAR_FREEZE_ANIM_GATE";

    private readonly AnimRequestGate _anim = new();
    private Func<object, object?>? _animComp, _controller;
    private MethodInfo? _castEcsComp;
    private int _animReplayed, _animLive;
    private Func<long, nint>? _controllerPtr;

    private void InstallAnimGate(HarmonyGameMethodHooker hooker)
    {
        if (Environment.GetEnvironmentVariable(AnimGateEnvVar) == "0") { _log.Info($"{Tag}animation request gate OFF ({AnimGateEnvVar}=0)"); return; }
        if (_types.FindType(AnimControllerType) is not { } ctl || !ResolveAnimLookup())
        {
            WarnOnce("animgate", "moving players and monsters may change pose while frozen (ECSAnimController not found)");
            return;
        }
        try
        {
            _animLive = AnimGatePatch.Install(hooker, ctl, _anim, m => WarnOnce(m, m));
            if (_types.FindType(GameEntityAccess.ManagerType) is { } mgr) hooker.PrefixAllOverloads(mgr, "RemoveEntity", OnAnimEntityLeaving);
        }
        catch (Exception ex) { WarnOnce("animgate", "animation request gate failed: " + ex.Message); }
    }

    private bool ResolveAnimLookup()
    {
        var model = _types.FindType(GameEntityAccess.ModelType);
        var ecsComp = _types.FindType(EcsAnimCompType);
        if (model is null || ecsComp is null) return false;
        _animComp = FastAccess.Getter<object?>(Stellar.Abstractions.Services.StellarInterop.FindPropertyUp(model, "AnimComp"));
        _controller = FastAccess.Getter<object?>(Stellar.Abstractions.Services.StellarInterop.FindPropertyUp(ecsComp, "controller_"));
        _castEcsComp = typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.TryCast))?.MakeGenericMethod(ecsComp);
        _controllerPtr = ControllerPtr;
        return _animComp is not null && _controller is not null && _castEcsComp is not null;
    }

    // Prefix chain on ZEntityMgr.RemoveEntity(long uuid, …): runs only when the removal really happens.
    private void OnAnimEntityLeaving(object? _, object?[] args)
    {
        if (_anim.Armed && args.Length > 0 && args[0] is long uuid) _anim.Untrack(uuid);
    }

    /// <summary>The press: every entity of this freeze's list gets its live controller tracked — nobody when the press does
    /// not know the local player (<see cref="AnimRequestGate.ArmFor"/>, qa M-7).</summary>
    private void TrackAnim()
    {
        if (_controllerPtr is null) return;
        AnimGatePatch.MainThread = _mainThread;
        _animReplayed = 0;
        _anim.ArmFor(_ledger, _ids, _controllerPtr);
    }

    /// <summary>An entity's live controller as a native pointer (0 = none / unreadable — warned once).</summary>
    private nint ControllerPtr(long uuid)
    {
        try { return ControllerOf(uuid)?.Pointer ?? IntPtr.Zero; }
        catch (Exception ex)
        {
            WarnOnce("animtrack", "could not read an entity's animation controller: " + ex.Message);
            return 0;
        }
    }

    private Il2CppObjectBase? ControllerOf(long uuid)
    {
        if (_entities.LiveModel(_entities.EntityByUuid(uuid)) is not { } m || _animComp!(m) is not { } comp) return null;
        return _castEcsComp!.Invoke(comp, null) is { } ecs ? _controller!(ecs) as Il2CppObjectBase : null;
    }

    /// <summary>Unfreeze: re-issues every kept request whose entity still serves the same controller, in order, through the
    /// game's own entry point (a pooled controller re-rented meanwhile is never driven). One failure never skips the rest.</summary>
    private void ReleaseAnim()
    {
        if (!_anim.Armed || _controllerPtr is null) return;
        var batch = _anim.TakeReplay(_controllerPtr);
        _anim.Replaying = true;
        try
        {
            foreach (var p in batch)
            {
                try
                {
                    p.Method.Invoke(p.Instance, p.Args);
                    _animReplayed++;
                }
                catch (Exception ex) { WarnOnce("animreplay", "could not replay an entity's animation: " + (ex.InnerException ?? ex).Message); }
            }
        }
        finally { _anim.Replaying = false; }
    }
}
