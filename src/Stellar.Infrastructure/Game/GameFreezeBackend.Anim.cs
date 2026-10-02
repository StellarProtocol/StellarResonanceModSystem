using System;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>Holds the animation REQUESTS of the press's entities while the clock is stopped (the rule and its evidence:
/// <see cref="AnimRequestGate"/>). A run-original gate on the ECS animation controller's state entry points
/// (<c>Panda.ZGame.ECSAnimController</c>: <c>PlayBaseState</c> / <c>PlayUpperState</c> / <c>PlayAdditiveState</c> /
/// <c>PlayManualClip</c> and their internal <c>play…</c> twins — enums, floats, a by-value <c>Vector2</c>, a string: nothing
/// <see cref="Il2CppPatchSafety"/> refuses) on the shared prefix trampoline. Tracked at the press: each entity's live model's
/// controller (never the local player or their own mount — excluded from <c>_ids</c> — and never a posing copy or NPC model,
/// which have controllers of their own). A leaving entity is untracked from the <c>RemoveEntity</c> prefix chain (it runs only
/// when the removal really happens). The kept requests are replayed on unfreeze, before the hold releases and the clock runs,
/// each only while its entity still serves the same controller. Installed on the first freeze; kill switch
/// <see cref="AnimGateEnvVar"/>=0. Main thread.</summary>
internal sealed partial class GameFreezeBackend
{
    internal const string AnimControllerType = "Panda.ZGame.ECSAnimController";
    internal const string EcsAnimCompType = "Panda.ZGame.ECSAnimComp";
    internal const string AnimGateEnvVar = "STELLAR_FREEZE_ANIM_GATE";

    private static readonly (string Name, AnimRequestGate.Layer? Layer)[] AnimEntryPoints =
    {
        ("PlayBaseState", AnimRequestGate.Layer.Base), ("playBaseState", AnimRequestGate.Layer.Base),
        ("PlayUpperState", AnimRequestGate.Layer.Upper), ("playUpperState", AnimRequestGate.Layer.Upper),
        ("PlayAdditiveState", AnimRequestGate.Layer.Additive), ("playAdditiveState", AnimRequestGate.Layer.Additive),
        ("PlayManualClip", null), ("playManualClip", null),   // the layer is the call's own EAnimLayer argument
    };

    private readonly AnimRequestGate _anim = new();
    private Func<object, object?>? _animComp, _controller;
    private MethodInfo? _castEcsComp;
    private int _animReplayed;
    private readonly System.Collections.Generic.Dictionary<(string, int), MethodInfo> _animMethods = new();

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
            foreach (var m in ctl.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var (name, _) in AnimEntryPoints)
                    if (m.Name == name) _animMethods[(name, m.GetParameters().Length)] = m;
            foreach (var (name, layer) in AnimEntryPoints)
            {
                var (n, l) = (name, layer);
                hooker.GatePrefixAllOverloads(ctl, n, (inst, args) => OnAnimRequest(n, inst, args, l));
            }
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
        return _animComp is not null && _controller is not null && _castEcsComp is not null;
    }

    // Gate on the controller's entry points. True = the game's call runs now.
    private bool OnAnimRequest(string name, object? instance, object?[] args, AnimRequestGate.Layer? layer)
    {
        if (!_anim.Armed || _anim.Replaying || instance is not Il2CppObjectBase ctl) return true;
        if (!_animMethods.TryGetValue((name, args.Length), out var method)) return true;   // an overload we cannot replay: runs
        var thread = Environment.CurrentManagedThreadId;
        return _anim.Decide(new AnimRequestGate.Request(ctl.Pointer, ctl, layer ?? ManualClipLayer(args), method, args,
            thread == 0 || thread != _mainThread));
    }

    private static AnimRequestGate.Layer ManualClipLayer(object?[] args)
    {
        try { return args.Length > 1 && args[1] is { } v ? (AnimRequestGate.Layer)Math.Clamp(Convert.ToInt32(v), 0, 2) : AnimRequestGate.Layer.Base; }
        catch { return AnimRequestGate.Layer.Base; }
    }

    // Prefix chain on ZEntityMgr.RemoveEntity(long uuid, …): runs only when the removal really happens.
    private void OnAnimEntityLeaving(object? _, object?[] args)
    {
        if (_anim.Armed && args.Length > 0 && args[0] is long uuid) _anim.Untrack(uuid);
    }

    /// <summary>The press: every entity of this freeze's list gets its live controller tracked.</summary>
    private void TrackAnim()
    {
        if (_animComp is null) return;
        _anim.Arm();
        _animReplayed = 0;
        foreach (var uuid in _ids)
        {
            try { if (ControllerOf(uuid) is { } c) _anim.Track(c.Pointer, uuid); }
            catch (Exception ex) { WarnOnce("animtrack", "could not hold an entity's animation: " + ex.Message); }
        }
    }

    private Il2CppObjectBase? ControllerOf(long uuid)
    {
        if (_entities.LiveModel(_entities.EntityByUuid(uuid)) is not { } m || _animComp!(m) is not { } comp) return null;
        return _castEcsComp!.Invoke(comp, null) is { } ecs ? _controller!(ecs) as Il2CppObjectBase : null;
    }

    /// <summary>Unfreeze: re-issues every kept request, in order, through the game's own entry point — only while its entity
    /// still serves the same controller (a pooled controller re-rented meanwhile is never driven). One failure never skips the
    /// rest.</summary>
    private void ReleaseAnim()
    {
        if (!_anim.Armed) return;
        var batch = _anim.TakeReplay();
        _anim.Replaying = true;
        try
        {
            foreach (var p in batch)
            {
                try
                {
                    if (ControllerOf(p.Uuid) is not { } now || now.Pointer != p.Controller) continue;
                    p.Method.Invoke(p.Instance, p.Args);
                    _animReplayed++;
                }
                catch (Exception ex) { WarnOnce("animreplay", "could not replay an entity's animation: " + (ex.InnerException ?? ex).Message); }
            }
        }
        finally { _anim.Replaying = false; }
    }
}
