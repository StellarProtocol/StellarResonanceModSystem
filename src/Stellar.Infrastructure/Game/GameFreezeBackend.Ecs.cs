using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>The ECS-layer animation freeze (<see cref="EcsSpeedGate"/>; owner MAIN evidence 2026-10-02: the boss kept
/// animating with its drawn speed and its controller speed both at 0). Every entity tracked by the drawn-speed gate also
/// has its model's ECS uid (<c>ZModel.EcsAnimComp.ecsUID</c>; GameObject models have none) tracked here, and on first
/// tracking the whole model is written to speed 0 once through the game's own call
/// (<c>ECSModelResourceManager.SetAnimatorLayerData(uid, -1, 0, 1)</c> — what <c>ECSAnimController.set_Speed(0)</c> itself
/// issues), so a layer the skill system had already started stops where it is. On release each model gets the game's own
/// whole-model write at its controller's CURRENT speed (0 while the controller's persist time is ≥ 0, exactly as
/// <c>set_Speed</c> decides), then each layer's latest wished speed (<see cref="EcsSpeedGate.TakeReleasePlan"/>) — after
/// the drawn speeds and factors are restored (<see cref="FreezeTeardown"/>), so the controller already carries the real
/// speed. A model whose uid changed (recycled) is skipped. Every per-entity step runs in its own try.</summary>
internal sealed partial class GameFreezeBackend
{
    internal const string EcsAnimCompType = "Panda.ZGame.ECSAnimComp";
    internal const string EcsControllerType = "Panda.ZGame.ECSAnimController";

    private readonly EcsSpeedGate _ecsGate = new();
    private readonly object[] _layerArgs = new object[4];
    private readonly Func<uint, long, float?> _ecsLiveController;
    private readonly Action<uint, EcsSpeedGate.LayerWrite> _ecsWrite;
    private Func<object, object?>? _ecsAnimComp, _ecsController;
    private Func<object, uint>? _ecsUid;
    private Func<object, float>? _ctlSpeed, _ctlPersist;
    private MethodInfo? _setLayer;
    private bool _ecsTried;
    private int _ecsPatched;

    /// <summary>Kill switch (read once, at install on the first freeze): <c>0</c> = the ECS layer gate is not installed.</summary>
    internal const string EcsGateEnvVar = "STELLAR_FREEZE_ECS_GATE";

    private void InstallEcsGate(HarmonyGameMethodHooker hooker)
    {
        if (Environment.GetEnvironmentVariable(EcsGateEnvVar) == "0") { _log.Info($"{Tag}ECS animation gate OFF ({EcsGateEnvVar}=0)"); return; }
        var mgr = _types.FindType(EcsSpeedPatch.ManagerType);
        if (mgr is null || !ResolveEcs()) { WarnOnce("ecsgate", "monsters may keep animating while frozen (ECS animator not found)"); return; }
        try
        {
            var installed = EcsSpeedPatch.Install(hooker, mgr, _ecsGate, m => WarnOnce("ecsgate:" + m, m), m => _log.Error(Tag + m));
            _ecsPatched = installed.Count;
            _log.Info($"{Tag}ECS speed writers installed: {installed}");
        }
        catch (Exception ex) { WarnOnce("ecsgate", "ECS animation gate failed: " + ex.Message); }
        if (_ecsPatched == 0) WarnOnce("ecsgate", "monsters may keep animating while frozen (ECS speed writers not patched)");
    }

    private bool ResolveEcs()
    {
        if (_ecsTried) return _setLayer is not null;
        var model = _types.FindType(GameEntityAccess.ModelType);
        var comp = _types.FindType(EcsAnimCompType);
        var ctl = _types.FindType(EcsControllerType);
        var mgr = _types.FindType(EcsSpeedPatch.ManagerType);
        if (model is null || comp is null || ctl is null || mgr is null) return false;   // not loaded yet: retried later
        _ecsTried = true;
        _ecsAnimComp = FastAccess.Getter<object?>(StellarInterop.FindPropertyUp(model, "EcsAnimComp"));
        _ecsUid = FastAccess.Getter<uint>(StellarInterop.FindPropertyUp(comp, "ecsUID"));
        _ecsController = FastAccess.Getter<object?>(StellarInterop.FindPropertyUp(comp, "controller_"));
        _ctlSpeed = FastAccess.Getter<float>(StellarInterop.FindPropertyUp(ctl, "Speed"));
        _ctlPersist = FastAccess.Getter<float>(StellarInterop.FindPropertyUp(ctl, "persistTime_"));
        var set = mgr.GetMethod("SetAnimatorLayerData", BindingFlags.Static | BindingFlags.Public, null,
            new[] { typeof(uint), typeof(int), typeof(float), typeof(float) }, null);
        if (_ecsAnimComp is null || _ecsUid is null || _ecsController is null || _ctlSpeed is null || _ctlPersist is null) return false;
        _setLayer = set;
        return set is not null;
    }

    /// <summary>The model's ECS uid, or 0 (a GameObject model, or unreadable).</summary>
    private uint EcsUid(object model)
    {
        if (!ResolveEcs()) return 0;
        try { return _ecsAnimComp!(model) is { } comp ? _ecsUid!(comp) : 0u; }
        catch { return 0; }
    }

    /// <summary>Tracks <paramref name="uuid"/>'s ECS model; the first time, the whole model goes to speed 0.</summary>
    private void TrackEcs(long uuid, object model)
    {
        if (_ledger.Excludes(uuid) || EcsUid(model) is not (> 0 and var uid)) return;
        if (_ecsGate.Track(uid, uuid)) WriteLayer(uid, EcsSpeedGate.WholeModel, 0f, 1f);
    }

    /// <summary>A model whose new owner is refused (the local player, their mount): never gated.</summary>
    private void ForgetEcs(object model)
    {
        if (EcsUid(model) is > 0 and var uid) _ecsGate.Forget(uid);
    }

    /// <summary>The teardown step: every tracked model released (<see cref="ReleaseEcs"/>), then forgotten.</summary>
    private void RestoreEcsLayers()
    {
        foreach (var (_, uuid) in _ecsGate.Snapshot()) ReleaseEcs(uuid);
        _ecsGate.Clear();
    }

    /// <summary>One entity's model written back and untracked (<see cref="EcsSpeedGate.Release"/>): the teardown, a
    /// mid-freeze release (excluded late, a deferred removal about to replay) and an entity LEAVING — so a pooled model is
    /// never reused with its layers at 0 (review 2026-10-02). Nothing is written unless the uid is still the entity's live
    /// model.</summary>
    private void ReleaseEcs(long uuid)
    {
        try
        {
            if (_ecsGate.Release(uuid, _ecsLiveController, _ecsWrite) is >= 0 and var writes) OnEcsReleased(uuid, writes);
        }
        catch (Exception ex) { WarnOnce("ecsrestore", "could not restore an entity's ECS animation: " + (ex.InnerException ?? ex).Message); }
    }

    /// <summary>The live model's controller speed when <paramref name="uid"/> is still <paramref name="uuid"/>'s live ECS
    /// model; null when it left, is destroying or was recycled (never written then).</summary>
    private float? EcsLiveControllerSpeed(uint uid, long uuid)
    {
        if (_entities.LiveModel(_entities.EntityByUuid(uuid)) is not { } m || _ecsAnimComp!(m) is not { } comp || _ecsUid!(comp) != uid) return null;
        return ControllerSpeed(comp);
    }

    /// <summary>The speed the controller would write itself: 0 while its persist time is ≥ 0, else its speed.</summary>
    private float ControllerSpeed(object ecsAnimComp)
    {
        if (_ecsController!(ecsAnimComp) is not { } ctl) return 1f;
        return _ctlPersist!(ctl) >= 0f ? 0f : _ctlSpeed!(ctl);
    }

    /// <summary>Our own <c>SetAnimatorLayerData</c> write: passes the ECS gate unrecorded.</summary>
    private void WriteLayer(uint uid, int layer, float speed, float weight)
    {
        _layerArgs[0] = uid;
        _layerArgs[1] = layer;
        _layerArgs[2] = speed;
        _layerArgs[3] = weight;
        _ecsGate.OwnWrite = true;
        try { _setLayer!.Invoke(null, _layerArgs); }
        catch (Exception ex) { WarnOnce("ecswrite", "could not write an ECS animation layer: " + (ex.InnerException ?? ex).Message); }
        finally { _ecsGate.OwnWrite = false; }
    }

    partial void OnEcsReleased(long uuid, int writes);
}
