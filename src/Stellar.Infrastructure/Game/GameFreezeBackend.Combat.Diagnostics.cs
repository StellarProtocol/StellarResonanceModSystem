using System;
using Stellar.Abstractions.Services;
using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Infrastructure.Hooks;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>The combat-freeze evidence capture's lifecycle (diagnostics only; owner reports 2026-10-02: in combat, frozen
/// monsters keep animating, walk / slide, their skill effects keep playing, and killed ones vanish). With diagnostics off
/// nothing here is constructed: every partial below returns on its first line, the set_Speed gate keeps its plain prefix
/// and no extra hook is installed. With diagnostics on: a census at the press, a 2 Hz bounded sampler for the first 60 s
/// of each freeze (<see cref="FreezeDiagClock"/>; .CombatSample / .CombatFx partials), the despawn sequence (.Despawn
/// partial), and a summary with hypothesis tags on unfreeze. Grammar: <c>[FreeCamDiag] …</c> lines (report
/// <c>.superpowers/sdd/posing/combat-diag-report.md</c>).</summary>
internal sealed partial class GameFreezeBackend
{
    private const string DiagTag = "[FreeCamDiag] ";

    private FreezeDiagClock? _diagClock;
    private FreezeDiagCounters? _diagCounters;
    private FreezeDiagSink? _diagSink;
    private FreezeDiagReader? _diagReader;
    private FreezeDiagCollections? _diagColls;
    private List<string> _diagPatched = new();
    private readonly HashSet<long> _diagTargets = new();
    private readonly Dictionary<long, FreezeDiagRow> _diagRows = new();
    private bool _diagExpiredLogged;

    partial void InstallCombatDiagCounters()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _diagClock = new FreezeDiagClock(enabled: true);
        _diagCounters = new FreezeDiagCounters(enabled: true);
        _diagReader = new FreezeDiagReader(_types, _entities);
        _diagColls = new FreezeDiagCollections(_types, _entities);
        var host = FastAccess.Getter<object?>(StellarInterop.FindPropertyUp(_types.FindType("Panda.ZGame.ZComponent"), "Host"));
        _diagSink = new FreezeDiagSink(_diagCounters, host, _entities.Uuid, () => _speedGate.MainThread);
        DrawnSpeedPatch.UseCounters(_diagCounters);
    }

    partial void InstallCombatDiagHooks(HarmonyGameMethodHooker hooker)
    {
        if (_diagSink is null) return;
        try { _diagPatched = FreezeDiagPatches.Install(hooker, _types, _diagSink, _log); }
        catch (Exception ex) { _log.Warning(DiagTag + "hook install failed: " + ex.Message); }
        InstallDespawnDiag(hooker);
        InstallFxDiag(hooker);
        _log.Info($"{DiagTag}hooks installed={_diagPatched.Count} [{string.Join(",", _diagPatched)}] gatePrefix=counted");
    }

    private partial bool DiagWantsLate() => _diagClock is { } c && c.WantsFrames(Environment.TickCount64);

    /// <summary>At the press (from <c>OnFrozen</c>): census, then the first sample at once.</summary>
    private void DiagBegin()
    {
        if (_diagClock is null || _diagCounters is null || _diagSink is null) return;
        var now = Environment.TickCount64;
        _diagClock.Start(now);
        _diagCounters.Reset();
        FreezeDiagPatches.Reset();
        _diagRows.Clear();
        _diagTargets.Clear();
        _diagTargets.UnionWith(_ids);
        _diagExpiredLogged = false;
        ResetFxDiag();
        ResetDespawnDiag();
        _diagSink.Begin();
        try { DiagCensus(); }
        catch (Exception ex) { _log.Warning(DiagTag + "census failed: " + ex.Message); }
    }

    partial void OnLateTickDiag(bool beforeHold)
    {
        if (_diagClock is null || _diagSink is null || !_diagClock.Active) return;
        if (!beforeHold)
        {
            _diagSink.InOwnHold = false;
            if (_holding) { _diagSink.HoldFrame = Time.frameCount; _diagSink.HoldTicks++; }
            return;
        }
        var now = Environment.TickCount64;
        if (_diagClock.Expired(now)) { DiagExpire(); return; }
        FlushFirstHits(now);
        if (_diagClock.Due(now))
        {
            try { DiagSample(now); }
            catch (Exception ex) { _log.Warning(DiagTag + "sample failed: " + ex.Message); }
        }
        _diagSink.InOwnHold = _holding;   // our hold write runs next: its set_Position calls are ours
    }

    private void DiagExpire()
    {
        if (_diagExpiredLogged) return;
        _diagExpiredLogged = true;
        _diagSink!.Active = false;
        _diagSink.InOwnHold = false;
        _log.Info($"{DiagTag}sampler stopped: {FreezeDiagClock.LifetimeMs / 1000} s cap reached (samples={_diagClock!.Samples}); counts so far stay for the summary");
    }

    private void FlushFirstHits(long now)
    {
        var pending = _diagSink!.PendingFirstHits;
        if (pending.Count == 0) return;
        foreach (var name in pending)
            if (_diagClock!.TakeEvent(now)) _log.Info($"{DiagTag}first hit: {name} t={_diagClock.Elapsed(now)}");
        pending.Clear();
    }

    /// <summary>On unfreeze (from <c>OnUnfreezing</c>, before any restore): per-entity summaries and the verdict line.</summary>
    private void DiagEnd()
    {
        if (_diagClock is null || _diagSink is null || _diagCounters is null) return;
        if (!_diagClock.Active) return;
        _diagSink.Active = false;
        try { DiagSummary(Environment.TickCount64); }
        catch (Exception ex) { _log.Warning(DiagTag + "summary failed: " + ex.Message); }
        _diagClock.Stop();
    }
}
