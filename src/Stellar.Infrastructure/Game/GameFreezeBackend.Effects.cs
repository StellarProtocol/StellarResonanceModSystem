using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>Effects (recon B): <c>ZEffectManager.SetEffectFreeze(uid, true)</c> over every key of <c>EffectDict</c> once per
/// freeze, plus a postfix on <c>AddEffectDisplay(ZEffect)</c> that freezes effects created while frozen. Every touched uid
/// is unfrozen on release.</summary>
internal sealed partial class GameFreezeBackend
{
    internal const string EffectManagerType = "Panda.ZEffect.ZEffectManager";
    internal const string EffectType = "Panda.ZEffect.ZEffect";

    private readonly SingletonAccess _fxManager = new();
    private readonly object[] _uidFreezeArgs = new object[2];
    private readonly object[] _boolArg = new object[1];
    private PropertyInfo? _effectDict, _fxUid;
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
        var fxFreeze = fx.GetMethod("SetEffectFreeze", new[] { typeof(bool) });
        if (_effectDict is null || _setEffectFreeze is null || _fxUid is null || fxFreeze is null) return false;
        _fxType = fx;
        _fxSetFreeze = fxFreeze;
        return true;
    }

    private void FreezeEffects()
    {
        if (!ResolveEffects()) { WarnOnce("fx", "effect freeze unavailable on this client"); return; }
        if (_fxManager.Get() is not { } mgr) return;
        foreach (var uid in EffectUids(mgr))
            if (SetEffectFreeze(mgr, uid, true)) _ledger.TouchEffect(uid);
    }

    private void UnfreezeEffects()
    {
        if (_ledger.Effects.Count == 0 || _fxManager.Get() is not { } mgr) return;
        foreach (var uid in _ledger.Effects) SetEffectFreeze(mgr, uid, false);
    }

    private bool SetEffectFreeze(object mgr, long uid, bool on)
    {
        _uidFreezeArgs[0] = uid;
        _uidFreezeArgs[1] = on;
        try { _setEffectFreeze!.Invoke(mgr, _uidFreezeArgs); return true; }
        catch { return false; }   // the effect ended meanwhile
    }

    // ZDictionary<long, ZEffect>.Keys → enumerator (MoveNext / Current), the path the probe used.
    private List<long> EffectUids(object mgr)
    {
        var uids = new List<long>();
        try
        {
            var dict = _effectDict!.GetValue(mgr);
            var keys = dict?.GetType().GetProperty("Keys")?.GetValue(dict);
            var en = keys?.GetType().GetMethod("GetEnumerator", Type.EmptyTypes)?.Invoke(keys, null);
            if (en is null) return uids;
            var move = en.GetType().GetMethod("MoveNext")!;
            var current = en.GetType().GetProperty("Current")!;
            while (move.Invoke(en, null) is true) uids.Add(Convert.ToInt64(current.GetValue(en)));
        }
        catch (Exception ex) { WarnOnce("fxkeys", "could not list effects: " + ex.Message); }
        return uids;
    }

    private void InstallEffectHook(HarmonyGameMethodHooker hooker)
    {
        var mgr = _types.FindType(EffectManagerType);
        if (mgr is null) { WarnOnce("fxhook", "effects created while frozen will not freeze (ZEffectManager not found)"); return; }
        try { hooker.PostfixAllOverloads(mgr, "AddEffectDisplay", OnEffectDisplayed); }
        catch (Exception ex) { WarnOnce("fxhook", "effect-creation hook failed: " + ex.Message); }
    }

    // Postfix on both AddEffectDisplay overloads; only the ZEffect one carries an effect to freeze.
    private void OnEffectDisplayed(object? _, object?[] args)
    {
        if (!_frozen || args.Length != 1 || args[0] is not { } fx || _fxType is null || !_fxType.IsInstanceOfType(fx)) return;
        try
        {
            _boolArg[0] = true;
            _fxSetFreeze!.Invoke(fx, _boolArg);
            _ledger.TouchEffect(Convert.ToInt64(_fxUid!.GetValue(fx)));
        }
        catch (Exception ex) { WarnOnce("fxnew", "could not freeze a new effect: " + ex.Message); }
    }
}
