using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

internal sealed partial class GameEffectVisibility
{
    internal const string EffectManagerType = "Panda.ZEffect.ZEffectManager";
    internal const string EffectType = "Panda.ZEffect.ZEffect";
    private readonly SingletonAccess _mgr = new();
    private readonly object[] _uidBool = new object[2];
    private readonly object[] _bool = new object[1];
    private readonly object[] _uid = new object[1];
    private PropertyInfo? _effectDict, _fxUid, _fxContext, _fxNeedDestroyed, _ctxFrom, _ctxBelong, _ctxVisible, _ctxAddr;
    private MethodInfo? _getEffect, _mgrSetVisible, _fxSetVisible;
    private Type? _fxType;
    private bool _hotUpdateReady, _unavailable;
    private HarmonyGameMethodHooker? _hooker;
    private bool _effectHooksInstalled;

    /// <summary>Hot-update ready: stores the hooker and resolves the reflection surface, but installs nothing yet
    /// (I4, spec § 3: "installed lazily on the first effect-layer hide"). <see cref="EnsureHooksInstalled"/>, called
    /// from <see cref="GameEffectVisibility.Apply"/>, does the actual patching the first time an effect layer is
    /// wanted.</summary>
    public void InstallHooks(HarmonyGameMethodHooker hooker)
    {
        _hooker = hooker;
        _hotUpdateReady = true;
        if (!Resolve()) WarnOnce("fxres", "effect hiding unavailable: ZEffectManager / ZEffect members not found");
    }

    /// <summary>Patches AddEffectDisplay(ZEffect) + ZEffect.Init once. Idempotent — safe to call on every Apply once
    /// an effect layer is wanted; the sweep already covers effects that existed before this runs.</summary>
    private void EnsureHooksInstalled()
    {
        if (_effectHooksInstalled || _hooker is not { } hooker) return;
        _effectHooksInstalled = true;
        try { hooker.PostfixAllOverloads(_types.FindType(EffectManagerType)!, "AddEffectDisplay", (_, args) => OnEffectCreated(args.Length == 1 ? args[0] : null, "AddEffectDisplay")); }
        catch (Exception ex) { WarnOnce("fxhook", "effect-display hook failed: " + ex.Message); }
        try { hooker.PostfixAllOverloads(_fxType!, "Init", (fx, _) => OnEffectCreated(fx, "Init")); }
        catch (Exception ex) { WarnOnce("fxinit", "effect-init hook failed: " + ex.Message); }
    }

    private bool Resolve()
    {
        if (_fxSetVisible is not null) return true;
        var mgr = _types.FindType(EffectManagerType);
        var fx = _types.FindType(EffectType);
        if (mgr is null || fx is null || !_mgr.Resolve(mgr)) { if (_hotUpdateReady) _unavailable = true; return false; }
        _effectDict = StellarInterop.FindPropertyUp(mgr, "EffectDict");
        _getEffect = mgr.GetMethod("GetEffect", new[] { typeof(long) });
        _mgrSetVisible = mgr.GetMethod("SetEffectVisible", new[] { typeof(long), typeof(bool) });
        _fxUid = StellarInterop.FindPropertyUp(fx, "Uid");
        _fxContext = StellarInterop.FindPropertyUp(fx, "Context");
        _fxNeedDestroyed = StellarInterop.FindPropertyUp(fx, "NeedDestroyed");
        var ctx = _fxContext?.PropertyType;
        _ctxFrom = ctx is null ? null : StellarInterop.FindPropertyUp(ctx, "FromUuid");
        _ctxBelong = ctx is null ? null : StellarInterop.FindPropertyUp(ctx, "BelongUuid");
        _ctxVisible = ctx is null ? null : StellarInterop.FindPropertyUp(ctx, "IsVisible");
        _ctxAddr = ctx is null ? null : StellarInterop.FindPropertyUp(ctx, "Addr");   // diagnostics-only (I2); never required below
        var fxSet = fx.GetMethod("SetEffectVisible", new[] { typeof(bool) });
        if (_effectDict is null || _getEffect is null || _mgrSetVisible is null || _fxUid is null || _fxContext is null ||
            _ctxFrom is null || _ctxVisible is null || fxSet is null)
        {
            if (_hotUpdateReady) _unavailable = true;
            return false;
        }
        _fxType = fx;
        _fxSetVisible = fxSet;
        return true;
    }

    private object? Manager() => _mgr.Get();
    private bool IsEffect(object fx) => _fxType is not null && _fxType.IsInstanceOfType(fx);
    private long ReadUid(object fx) => Convert.ToInt64(_fxUid!.GetValue(fx));

    /// <summary>Caster = FromUuid, else BelongUuid (Task 0); visible = Context.IsVisible. An unreadable context reads
    /// as caster 0 (never hidden).</summary>
    private (long Caster, long From, long Belong, bool Visible) ReadContext(object fx)
    {
        if (_fxContext!.GetValue(fx) is not { } c) return (0, 0, 0, false);
        var from = Convert.ToInt64(_ctxFrom!.GetValue(c));
        var belong = _ctxBelong is null ? 0L : Convert.ToInt64(_ctxBelong.GetValue(c));
        return (from != 0 ? from : belong, from, belong, _ctxVisible!.GetValue(c) is true);
    }

    /// <summary>M1 / perf: visibility only, for Sweep's common held-and-still-hidden case — skips the caster field
    /// reads <see cref="ReadContext"/> also does, which a re-hide check never needs.</summary>
    private bool ReadVisible(object fx) => _fxContext!.GetValue(fx) is { } c && _ctxVisible!.GetValue(c) is true;

    /// <summary>I2(a)/(b)/(c), diagnostics only: the effect's asset id (<c>Context.Addr</c>, cached like the other
    /// context fields in <see cref="Resolve"/>) and, if present, a path/name property (resolved by name fresh each
    /// call — only ever reached from the bounded diagnostic log paths, never production).</summary>
    private (uint Addr, string? Path) ReadAsset(object fx)
    {
        if (_fxContext?.GetValue(fx) is not { } c) return (0, null);
        uint addr = 0;
        try { if (_ctxAddr?.GetValue(c) is { } a) addr = Convert.ToUInt32(a); } catch { /* diagnostics only */ }
        string? path = null;
        try
        {
            var p = StellarInterop.FindPropertyUp(c.GetType(), "Path") ?? StellarInterop.FindPropertyUp(c.GetType(), "Name");
            path = p?.GetValue(c)?.ToString();
        }
        catch { /* diagnostics only */ }
        return (addr, path);
    }

    /// <summary>I2(a), diagnostics only: OwnerUuid / AttackerUuid / HostUuid off the effect's context, resolved by
    /// name — a property absent on this context type (or a failed read) comes back null, never a thrown exception.</summary>
    private (long? Owner, long? Attacker, long? Host) ReadZeroCasterIds(object fx)
    {
        if (_fxContext?.GetValue(fx) is not { } c) return (null, null, null);
        var t = c.GetType();
        return (ReadLongPropertyOrNull(c, t, "OwnerUuid"), ReadLongPropertyOrNull(c, t, "AttackerUuid"), ReadLongPropertyOrNull(c, t, "HostUuid"));
    }

    private static long? ReadLongPropertyOrNull(object instance, Type t, string name)
    {
        try { return StellarInterop.FindPropertyUp(t, name) is { } p ? Convert.ToInt64(p.GetValue(instance)) : null; }
        catch { return null; }
    }

    private object? Effect(object mgr, long uid)
    {
        _uid[0] = uid;
        try { return _getEffect!.Invoke(mgr, _uid); }
        catch { return null; }
    }

    private bool SetManagerVisible(object mgr, long uid, bool visible)
    {
        _uidBool[0] = uid;
        _uidBool[1] = visible;
        try { _mgrSetVisible!.Invoke(mgr, _uidBool); return true; }
        catch { return false; }   // the effect ended meanwhile
    }

    private bool SetInstanceVisible(object fx, bool visible)
    {
        if (IsDestroyed(fx)) return false;
        _bool[0] = visible;
        try { _fxSetVisible!.Invoke(fx, _bool); return true; }
        catch { return false; }
    }

    private bool IsDestroyed(object fx)
    {
        if (fx is not Il2CppObjectBase { WasCollected: false }) return true;
        try { return _fxNeedDestroyed?.GetValue(fx) is true; }
        catch { return true; }
    }

    // ZDictionary<long, ZEffect>.Keys → enumerator (the path the freeze used). Null = not listable this time.
    private HashSet<long>? ListUids(object mgr)
    {
        try
        {
            var dict = _effectDict!.GetValue(mgr);
            var keys = dict?.GetType().GetProperty("Keys")?.GetValue(dict);
            var en = keys?.GetType().GetMethod("GetEnumerator", Type.EmptyTypes)?.Invoke(keys, null);
            if (en is null) return null;
            var move = en.GetType().GetMethod("MoveNext")!;
            var current = en.GetType().GetProperty("Current")!;
            var uids = new HashSet<long>();
            while (move.Invoke(en, null) is true) uids.Add(Convert.ToInt64(current.GetValue(en)));
            return uids;
        }
        catch (Exception ex) { WarnOnce("fxkeys", "could not list effects: " + ex.Message); return null; }
    }
}
