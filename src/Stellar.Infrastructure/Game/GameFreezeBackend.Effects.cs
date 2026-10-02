using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>Effects (recon B): <c>ZEffectManager.SetEffectFreeze(uid, true)</c> over every key of <c>EffectDict</c> once per
/// freeze, plus postfixes on <c>AddEffectDisplay(ZEffect)</c> and <c>ZEffect.Init</c> that freeze effects created while
/// frozen (<see cref="FreezeEffectRule"/>). Every touched uid is unfrozen on release — through the manager when it is in
/// <c>EffectDict</c>, else through the instance kept when it was frozen, guarded (<see cref="FreezeEffectRule.Unfreeze"/>).</summary>
internal sealed partial class GameFreezeBackend
{
    internal const string EffectManagerType = "Panda.ZEffect.ZEffectManager";
    internal const string EffectType = "Panda.ZEffect.ZEffect";

    private readonly SingletonAccess _fxManager = new();
    private readonly object[] _uidFreezeArgs = new object[2];
    private readonly object[] _boolArg = new object[1];
    private readonly Dictionary<long, object> _fxInstances = new();   // instance-frozen effects, by uid (this freeze only)
    private PropertyInfo? _effectDict, _fxUid, _fxNeedDestroyed;
    private MethodInfo? _setEffectFreeze, _fxSetFreeze;
    private Type? _fxType;

    private bool ResolveEffects()
    {
        if (_fxSetFreeze is not null) return true;
        var mgr = _types.FindType(EffectManagerType);
        var fx = _types.FindType(EffectType);
        if (mgr is null || fx is null || !_fxManager.Resolve(mgr)) return false;
        _effectDict = StellarInterop.FindPropertyUp(mgr, "EffectDict");
        _setEffectFreeze = mgr.GetMethod("SetEffectFreeze", new[] { typeof(long), typeof(bool) });
        _fxUid = StellarInterop.FindPropertyUp(fx, "Uid");
        _fxNeedDestroyed = StellarInterop.FindPropertyUp(fx, "NeedDestroyed");   // absent: no instance is ever unfrozen
        var fxFreeze = fx.GetMethod("SetEffectFreeze", new[] { typeof(bool) });
        if (_effectDict is null || _setEffectFreeze is null || _fxUid is null || fxFreeze is null) return false;
        _fxType = fx;
        _fxSetFreeze = fxFreeze;
        return true;
    }

    private void FreezeEffects()
    {
        if (!ResolveEffects()) { WarnOnce("fx", "effect freeze unavailable on this client"); return; }
        _fxInstances.Clear();
        if (_fxManager.Get() is not { } mgr) return;
        foreach (var uid in EffectUids(mgr) ?? new List<long>())
            if (SetEffectFreeze(mgr, uid, true)) _ledger.TouchEffect(uid);
    }

    /// <summary>The teardown step. No manager = the scene's effects are gone: no kept instance is touched.</summary>
    private void UnfreezeEffects()
    {
        try
        {
            if (_ledger.Effects.Count == 0 || _fxManager.Get() is not { } mgr) return;
            var listed = EffectUids(mgr);
            var counts = FreezeEffectRule.Unfreeze(_ledger.Effects, listed is null ? null : new HashSet<long>(listed),
                FxInstance, uid => SetEffectFreeze(mgr, uid, false), UnfreezeInstance);
            OnEffectsUnfrozen(counts);
        }
        finally { _fxInstances.Clear(); }
    }

    /// <summary>The instance kept for <paramref name="uid"/>, read now: its uid and whether it is being destroyed (an
    /// unreadable or collected one reads as destroyed — never touched). Null when none was kept.</summary>
    private (long Uid, bool Destroyed)? FxInstance(long uid)
    {
        if (!_fxInstances.TryGetValue(uid, out var fx)) return null;
        if (fx is not Il2CppObjectBase { WasCollected: false } || _fxNeedDestroyed is null) return (0L, true);
        try { return (Convert.ToInt64(_fxUid!.GetValue(fx)), _fxNeedDestroyed.GetValue(fx) is true); }
        catch { return (0L, true); }
    }

    private void UnfreezeInstance(long uid)
    {
        if (!_fxInstances.TryGetValue(uid, out var fx)) return;
        _boolArg[0] = false;
        try { _fxSetFreeze!.Invoke(fx, _boolArg); }
        catch (Exception ex) { WarnOnce("fxunfreezeone", "could not unfreeze an effect: " + (ex.InnerException ?? ex).Message); }
    }

    private bool SetEffectFreeze(object mgr, long uid, bool on)
    {
        _uidFreezeArgs[0] = uid;
        _uidFreezeArgs[1] = on;
        try { _setEffectFreeze!.Invoke(mgr, _uidFreezeArgs); return true; }
        catch { return false; }   // the effect ended meanwhile
    }

    // ZDictionary<long, ZEffect>.Keys → enumerator (MoveNext / Current), the path the probe used. Null = not listable.
    private List<long>? EffectUids(object mgr)
    {
        var uids = new List<long>();
        try
        {
            var dict = _effectDict!.GetValue(mgr);
            var keys = dict?.GetType().GetProperty("Keys")?.GetValue(dict);
            var en = keys?.GetType().GetMethod("GetEnumerator", Type.EmptyTypes)?.Invoke(keys, null);
            if (en is null) return null;
            var move = en.GetType().GetMethod("MoveNext")!;
            var current = en.GetType().GetProperty("Current")!;
            while (move.Invoke(en, null) is true) uids.Add(Convert.ToInt64(current.GetValue(en)));
        }
        catch (Exception ex) { WarnOnce("fxkeys", "could not list effects: " + ex.Message); return null; }
        return uids;
    }

    private void InstallEffectHook(HarmonyGameMethodHooker hooker)
    {
        var mgr = _types.FindType(EffectManagerType);
        if (mgr is null) { WarnOnce("fxhook", "effects created while frozen will not freeze (ZEffectManager not found)"); return; }
        try { hooker.PostfixAllOverloads(mgr, "AddEffectDisplay", OnEffectDisplayed); }
        catch (Exception ex) { WarnOnce("fxhook", "effect-creation hook failed: " + ex.Message); }
        // ZEffect.Init(EffectContext): every effect's own initialisation (combat-freeze fix 2026-10-02 — creations the
        // AddEffectDisplay postfix never sees; FreezeEffectRule).
        if (_types.FindType(EffectType) is not { } fx) return;
        try { hooker.PostfixAllOverloads(fx, "Init", OnEffectInit); }
        catch (Exception ex) { WarnOnce("fxinit", "effect-init hook failed: " + ex.Message); }
    }

    // Postfix on ZEffect.Init(EffectContext): the instance is the new effect (its uid set from the context by now).
    private void OnEffectInit(object? fx, object?[] args)
    {
        if (!_frozen || fx is null || _fxSetFreeze is null || Environment.CurrentManagedThreadId != _speedGate.MainThread) return;
        try
        {
            var uid = Convert.ToInt64(_fxUid!.GetValue(fx));
            if (uid == 0) return;   // no uid yet: it could never be unfrozen — AddEffectDisplay still catches it
            if (!FreezeEffectRule.ShouldFreeze(_frozen, _ledger.Effects.Contains(uid), 0L, _ledger.Self)) return;
            _boolArg[0] = true;
            _fxSetFreeze.Invoke(fx, _boolArg);
            _ledger.TouchEffect(uid);
            _fxInstances[uid] = fx;   // maybe never in EffectDict: unfrozen through this instance (guarded)
            OnEffectInitFrozen();
        }
        catch (Exception ex) { WarnOnce("fxinitone", "could not freeze an initialising effect: " + (ex.InnerException ?? ex).Message); }
    }

    partial void OnEffectInitFrozen();
    partial void OnEffectsUnfrozen(FreezeEffectRule.UnfreezeCounts counts);

    // Postfix on both AddEffectDisplay overloads; only the ZEffect one carries an effect to freeze.
    private void OnEffectDisplayed(object? _, object?[] args)
    {
        if (!_frozen || args.Length != 1 || args[0] is not { } fx || _fxType is null || !_fxType.IsInstanceOfType(fx)) return;
        try
        {
            _boolArg[0] = true;
            _fxSetFreeze!.Invoke(fx, _boolArg);
            var uid = Convert.ToInt64(_fxUid!.GetValue(fx));
            _ledger.TouchEffect(uid);
            if (uid != 0) _fxInstances[uid] = fx;
        }
        catch (Exception ex) { WarnOnce("fxnew", "could not freeze a new effect: " + ex.Message); }
    }
}
