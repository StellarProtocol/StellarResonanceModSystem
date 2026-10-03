using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game;

internal sealed partial class GameEffectVisibility
{
    private const int MaxClassifiedLines = 400;
    private const int MaxClassifiedWithCasterUids = 300;
    private const int MaxZeroCasterUids = 50;   // I2(a)
    private const int MaxBornHiddenUids = 50;   // I2(c)
    private int _classifiedLines;
    // First-sighting cap keyed by uid, not a per-call counter: Sweep re-classifies every listed uid on every
    // Apply/Reassert, so a counter of calls fills the budget in a handful of sweeps and a live effect further
    // down the list (a summon's, a party member's) is never logged. Bounded at MaxClassifiedWithCasterUids entries.
    private readonly HashSet<long> _classifiedWithCasterUids = new();
    private readonly HashSet<long> _zeroCasterUids = new();   // I2(a): distinct zero-caster uids logged this session
    // I2(c): distinct (uid, hook) pairs logged this session — follow-up: keyed per hook, not per uid, so an effect
    // that is normally invisible at Init but visible by AddEffectDisplay is told apart from a genuine born-hidden one.
    private readonly HashSet<(long Uid, string Hook)> _bornHiddenUids = new();
    private int _mineHidden, _partyHidden, _othersHidden, _monstersHidden;   // reset every OnSwept — per-sweep, not cumulative

    partial void OnClassified(long uid, (long Caster, long From, long Belong, bool Visible) ctx, VisibilityLayers owner, bool viaInstance, object fx)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        LogUnresolved(uid, ctx.From, ctx.Belong, owner);
        LogClassifiedWithCaster(uid, ctx, owner, viaInstance, fx);
    }

    private void LogUnresolved(long uid, long from, long belong, VisibilityLayers owner)
    {
        if (owner != VisibilityLayers.None || _classifiedLines >= MaxClassifiedLines) return;
        if (from == 0 && belong == 0) return;   // scenery: expected, not interesting
        _classifiedLines++;
        _log.Info($"[EffectHide] unresolved uid={uid} from={from} belong={belong}");
    }

    /// <summary>The owner's in-game pass could not be run for this change — this is its replacement: the first 300
    /// DISTINCT classified effects that had a caster (one line per uid, the first time it is seen), so a wrong
    /// owner can be spotted straight from the log (never from the unresolved-only line above, which only fires when
    /// owner is None) without the budget being consumed by the same handful of long-lived effects re-classified on
    /// every sweep. I2(b): carries visible/via/addr too, so a line can be matched to what is on screen.</summary>
    private void LogClassifiedWithCaster(long uid, (long Caster, long From, long Belong, bool Visible) ctx, VisibilityLayers owner, bool viaInstance, object fx)
    {
        if (ctx.From == 0 && ctx.Belong == 0) return;   // scenery: never logged, never counted
        if (_classifiedWithCasterUids.Contains(uid)) return;   // already logged this uid once
        if (_classifiedWithCasterUids.Count >= MaxClassifiedWithCasterUids) return;   // budget exhausted
        _classifiedWithCasterUids.Add(uid);
        var (addr, _) = ReadAsset(fx);
        var via = viaInstance ? "hook" : "sweep";
        _log.Info($"[EffectHide] classified uid={uid} from={ctx.From}({Kind(ctx.From)}) belong={ctx.Belong}({Kind(ctx.Belong)}) " +
                  $"owner={owner} visible={ctx.Visible} via={via} addr={addr}");
    }

    /// <summary>I2(a): the owner's § 5 Q1 pass could not be run on the test client — this is its replacement for the
    /// "scenery" case specifically (FromUuid==0 && BelongUuid==0, which the classified/unresolved lines above both
    /// skip as expected). Logs whatever caster-shaped fields the context DOES carry so a genuinely-scenery effect can
    /// be told apart from one whose caster lives in a field this framework doesn't read yet. Capped at
    /// <see cref="MaxZeroCasterUids"/> distinct uids.</summary>
    partial void OnZeroCaster(long uid, object fx)
    {
        if (!StellarDiagnostics.IsEnabled || uid == 0) return;
        if (_zeroCasterUids.Contains(uid) || _zeroCasterUids.Count >= MaxZeroCasterUids) return;
        _zeroCasterUids.Add(uid);
        var (addr, path) = ReadAsset(fx);
        var (owner, attacker, host) = ReadZeroCasterIds(fx);
        _log.Info($"[EffectHide] zero-caster uid={uid} addr={addr} owner={Fmt(owner)} attacker={Fmt(attacker)} " +
                  $"host={Fmt(host)} path={path ?? "absent"}");
    }

    private static string Fmt(long? v) => v?.ToString() ?? "absent";

    /// <summary>Simple, low-bits-only classification (EntityId's own 640=player / 64,32832=monster markers) for a log
    /// label. Deliberately never relabels a caster "self" from <paramref name="uuid"/>'s own uuid — <c>owner</c> is
    /// printed separately so summon resolution (a monster-uuid caster whose owner is EffectsMine) stays visible.</summary>
    private static string Kind(long uuid)
    {
        if (uuid == 0) return "none";
        var low = uuid & 0xFFFF;
        if (low == 640) return "player";
        if (low == 64 || low == 32832) return "monster";
        return $"other(low={low})";
    }

    /// <summary>I2(c), pool reuse: runs BEFORE OnEffectCreated's early return, so this covers an effect being
    /// recycled even when no effect layer is currently wanted. An effect whose context already reads invisible at
    /// <paramref name="hook"/> ("Init" or "AddEffectDisplay") means the pool handed back a wrapper the game (or an
    /// earlier hide) had already hidden — `recorded` says whether OUR ledger already knows about it (a pooled
    /// wrapper's uid can be reused across effects, so a stale `_instances`/ledger entry would otherwise look like a
    /// live hide). Follow-up: dedupes per (uid, hook) rather than per uid alone, so an effect that is normally
    /// invisible at Init but visible by the time AddEffectDisplay runs is told apart from one that is really born
    /// hidden at both. Capped at <see cref="MaxBornHiddenUids"/> distinct (uid, hook) pairs; must cost nothing when
    /// diagnostics are off — the IsEnabled check is the very first thing this partial method does.</summary>
    partial void OnEffectSeen(object? fx, string hook)
    {
        if (!StellarDiagnostics.IsEnabled || fx is null) return;
        if (_bornHiddenUids.Count >= MaxBornHiddenUids) return;
        if (System.Environment.CurrentManagedThreadId != _mainThread || !IsEffect(fx)) return;
        try
        {
            var uid = ReadUid(fx);
            var key = (uid, hook);
            if (uid == 0 || _bornHiddenUids.Contains(key) || ReadVisible(fx)) return;
            _bornHiddenUids.Add(key);
            var (addr, _) = ReadAsset(fx);
            _log.Info($"[EffectHide] born-hidden uid={uid} hook={hook} addr={addr} recorded={_ledger.Contains(uid)}");
        }
        catch { /* diagnostics only */ }
    }

    partial void OnSweepHidden(VisibilityLayers owner)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        switch (owner)
        {
            case VisibilityLayers.EffectsMine: _mineHidden++; break;
            case VisibilityLayers.EffectsParty: _partyHidden++; break;
            case VisibilityLayers.EffectsOthers: _othersHidden++; break;
            case VisibilityLayers.EffectsMonsters: _monstersHidden++; break;
        }
    }

    partial void OnSwept(VisibilityLayers wanted, int hidden, int shown, int held)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[EffectHide] apply wanted={wanted} hidden={hidden} shown={shown} held={held} " +
                  $"mine={_mineHidden} party={_partyHidden} others={_othersHidden} monsters={_monstersHidden}");
        _mineHidden = _partyHidden = _othersHidden = _monstersHidden = 0;
    }
}
