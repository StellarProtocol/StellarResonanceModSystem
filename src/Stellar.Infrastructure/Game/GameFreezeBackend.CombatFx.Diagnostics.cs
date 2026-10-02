using System;
using Stellar.Abstractions.Services;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Diagnostics;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>Effects in the combat-freeze capture (diagnostics only; owner evidence 2026-10-02: frozen monsters' skill
/// effects keep playing). A chained postfix on <c>AddEffectDisplay</c> (after the freeze's own) counts creations through
/// the hooked path per owner (<c>EffectContext.BelongUuid</c>) and through the <c>EffectBinder_Runtime</c> overload the
/// freeze ignores; once a second the <c>fx</c> census walks <c>EffectDict</c> and counts effects still unfrozen — touched
/// by the freeze (so the game un-froze them) or never touched (created by an unhooked path) — and who owns them.</summary>
internal sealed partial class GameFreezeBackend
{
    private const int FxCensusCap = 600;
    private const string EffectContextType = "Panda.ZGame.EffectContext";

    private MethodInfo? _fxGetCtx;
    private PropertyInfo? _fxCtx, _ctxFrozen, _ctxBelong;
    private readonly object[] _fxUidArg = new object[1];
    private int _diagFxMissedMax, _diagFxUnfrozenMax;

    private void InstallFxDiag(HarmonyGameMethodHooker hooker)
    {
        var mgr = _types.FindType(EffectManagerType);
        var ctx = _types.FindType(EffectContextType);
        if (mgr is null || ctx is null) { _log.Info(DiagTag + "fx diag off (ZEffectManager / EffectContext not found)"); return; }
        _fxGetCtx = mgr.GetMethod("GetEffectContext", new[] { typeof(long) });
        _fxCtx = StellarInterop.FindPropertyUp(_types.FindType(EffectType), "Context");
        _ctxFrozen = StellarInterop.FindPropertyUp(ctx, "IsFreeze");
        _ctxBelong = StellarInterop.FindPropertyUp(ctx, "BelongUuid");
        try { hooker.PostfixAllOverloads(mgr, "AddEffectDisplay", OnEffectDisplayedDiag); }
        catch (Exception ex) { _log.Warning(DiagTag + "fx display hook failed: " + ex.Message); }
    }

    private void ResetFxDiag() => _diagFxMissedMax = _diagFxUnfrozenMax = 0;

    // Chained after OnEffectDisplayed (the freeze's own postfix): the ZEffect overload carries the effect, the other the binder.
    private void OnEffectDisplayedDiag(object? _, object?[] args)
    {
        if (!StellarDiagnostics.IsEnabled || _diagSink is null || !_diagSink.Enter() || args.Length != 1 || args[0] is not { } fx) return;
        if (_fxType is null || !_fxType.IsInstanceOfType(fx)) { _diagSink.Hit(0L, DiagSlot.FxBinder); return; }
        long owner = 0;
        try { if (_fxCtx?.GetValue(fx) is { } ctx && _ctxBelong?.GetValue(ctx) is { } b) owner = Convert.ToInt64(b); }
        catch { /* owner unknown: counted global */ }
        _diagSink.Hit(owner, DiagSlot.FxDisplay);
    }

    private void DiagFxCensus(long now)
    {
        if (_fxGetCtx is null || _ctxFrozen is null || !ResolveEffects() || _fxManager.Get() is not { } mgr) return;
        int total = 0, frozen = 0, touchedOpen = 0, newOpen = 0, newOnWatched = 0;
        var owners = new Dictionary<long, int>();
        foreach (var uid in (EffectUids(mgr) ?? new List<long>()).Take(FxCensusCap))
        {
            if (FxContext(mgr, uid) is not { } ctx || FxRead(ctx) is not { } read) continue;
            total++;
            if (read.Frozen) { frozen++; continue; }
            var owner = read.Owner;
            owners[owner] = owners.TryGetValue(owner, out var n) ? n + 1 : 1;
            if (_ledger.Effects.Contains(uid)) touchedOpen++;
            else { newOpen++; if (_diagCounters!.IsWatched(owner)) newOnWatched++; }
        }
        _diagFxMissedMax = Math.Max(_diagFxMissedMax, newOpen);
        _diagFxUnfrozenMax = Math.Max(_diagFxUnfrozenMax, touchedOpen);
        if (!_diagClock!.TakeLine(now)) return;
        var top = owners.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}{(_diagCounters!.IsWatched(kv.Key) ? "*" : "")}:{kv.Value}");
        _log.Info($"{DiagTag}fx s={_diagClock.Samples} total={total} frozen={frozen} unfrozenTouched={touchedOpen} unfrozenNew={newOpen} " +
                  $"newOnWatched={newOnWatched} ledger={_ledger.Effects.Count} owners=[{string.Join(",", top)}]");
    }

    private (bool Frozen, long Owner)? FxRead(object ctx)
    {
        try { return (_ctxFrozen!.GetValue(ctx) is true, _ctxBelong?.GetValue(ctx) is { } b ? Convert.ToInt64(b) : 0L); }
        catch { return null; }   // the effect ended meanwhile
    }

    private object? FxContext(object mgr, long uid)
    {
        _fxUidArg[0] = uid;
        try { return _fxGetCtx!.Invoke(mgr, _fxUidArg); }
        catch { return null; }   // the effect ended meanwhile
    }
}
