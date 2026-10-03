using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game;

/// <summary>Effect layers (spec § 3): on every change of the wanted set, show what we hid that is no longer wanted, then
/// sweep ZEffectManager.EffectDict and hide every visible effect whose caster's group is wanted; creation hooks hide new
/// effects while any effect layer is wanted (AddEffectDisplay + ZEffect.Init — the free-camera recon found AddEffectDisplay
/// alone misses some). Effects hidden through their instance (never listed in EffectDict) are shown through it again.
/// Fails open: unresolved game types → nothing hidden, one warning. Main thread only.</summary>
internal sealed partial class GameEffectVisibility
{
    private const string Tag = "[PhotoStudio] ";
    // I1: bounded prune threshold for the creation-hook path (PruneInstancesOverBudget) — see InstancePruneRule.
    private const int MaxInstances = 256;
    private static readonly HashSet<long> EmptyListed = new();   // asks ReleaseState about the instance alone (never "listed")
    // Follow-up: hysteresis so a sustained raid (> MaxInstances live entries) doesn't re-pay the O(n) prune pass on
    // every single creation-hook call (Init AND AddEffectDisplay both call OnEffectCreated — twice per spawn).
    // Grows via InstancePruneRule.NextThreshold after every pass; never shrinks below MaxInstances.
    private int _pruneAt = MaxInstances;
    private readonly IGameTypeRegistry _types;
    private readonly Func<long, VisibilityLayers> _classify;
    private readonly IPluginLog _log;
    private readonly EffectHideLedger _ledger = new();
    private readonly Dictionary<long, object> _instances = new();   // hidden via the instance, by uid
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly int _mainThread = Environment.CurrentManagedThreadId;   // constructed in Load() on the main thread
    private VisibilityLayers _wanted;

    public GameEffectVisibility(IGameTypeRegistry types, Func<long, VisibilityLayers> classify, IPluginLog log)
    {
        _types = types;
        _classify = classify;
        _log = log;
    }

    public VisibilityLayers Available => _unavailable ? VisibilityLayers.None : VisibilityLayerSets.Effects;

    /// <summary>M2: true when a release is still owed even though nothing is wanted any more — Apply couldn't show
    /// the ledger's uids back (manager missing / listing unreadable) this tick. Lets
    /// <see cref="GameVisibilityBackend.HasPendingRestore"/> ask for a retry via Reassert instead of stranding them.</summary>
    public bool HasPendingRelease => _ledger.Count > 0 && _wanted == VisibilityLayers.None;

    public VisibilityLayers Apply(VisibilityLayers requested)
    {
        _wanted = requested & VisibilityLayerSets.Effects;
        if (_unavailable) return VisibilityLayers.None;   // already known dead on this client — don't re-resolve every call
        if (_wanted == VisibilityLayers.None && _ledger.Count == 0) return VisibilityLayers.None;
        if (!Resolve()) { if (_wanted != 0) WarnOnce("fx", "effect hiding unavailable on this client"); return VisibilityLayers.None; }
        if (_wanted != VisibilityLayers.None) EnsureHooksInstalled();   // I4: lazy — first effect-layer hide, not hot-update-ready
        if (Manager() is not { } mgr) { if (_wanted != 0) WarnOnce("fx", "effect hiding unavailable on this client"); return VisibilityLayers.None; }
        var listed = ListUids(mgr);
        var shown = listed is null ? 0 : Release(mgr, listed);
        PruneStaleInstances();   // I1: TakeReleasable may have dropped an ended uid from the ledger above
        var hidden = _wanted == VisibilityLayers.None || listed is null ? 0 : Sweep(mgr, listed);
        OnSwept(_wanted, hidden, shown, _ledger.Count);
        return _wanted;
    }

    /// <summary>Shows back whatever the ledger holds that is no longer wanted and still alive. Only called with a
    /// REAL listing — a failed listing means "don't know", so <see cref="Apply"/> skips this entirely and every
    /// held uid is kept for the next apply (never dropped as ended just because the listing failed this tick).</summary>
    private int Release(object mgr, HashSet<long> listed)
    {
        var shown = 0;
        foreach (var uid in _ledger.TakeReleasable(_wanted, uid => IsAlive(listed, uid)))
        {
            try { if (TryShow(mgr, listed, uid)) shown++; }
            catch (Exception ex) { WarnOnce("fxshow", "could not show a released effect: " + (ex.InnerException ?? ex).Message); }
        }
        return shown;
    }

    private int Sweep(object mgr, HashSet<long> listed)
    {
        var hidden = 0;
        foreach (var uid in listed)
        {
            try { if (SweepOne(mgr, uid)) hidden++; }
            catch (Exception ex) { WarnOnce("fxsweep", "effect sweep failed for uid " + uid + ": " + (ex.InnerException ?? ex).Message); }
        }
        return hidden;
    }

    /// <summary>One sweep step. M1 + perf: a uid the ledger already holds costs one visibility read (re-hide it if
    /// the game showed it again — e.g. a cutscene ending resets effect visibility) instead of the full
    /// classify-and-hide path everything else goes through.</summary>
    private bool SweepOne(object mgr, long uid) =>
        _ledger.Contains(uid) ? ReHideIfNeeded(mgr, uid) : Effect(mgr, uid) is { } fx && TryHide(fx, uid, viaInstance: false, mgr);

    /// <summary>M1: re-hides a held uid the game re-showed. <see cref="EffectHideLedger.ShouldReHide"/> is the pure
    /// decision; this only reads the one field it needs (<see cref="ReadVisible"/>) rather than the full context
    /// <see cref="ReadContext"/> reads for a fresh classify.</summary>
    private bool ReHideIfNeeded(object mgr, long uid)
    {
        if (Effect(mgr, uid) is not { } fx) return false;
        if (!EffectHideLedger.ShouldReHide(held: true, ReadVisible(fx))) return false;
        if (!SetManagerVisible(mgr, uid, false)) return false;
        if (_ledger.TryGetOwner(uid, out var owner)) OnSweepHidden(owner);
        return true;
    }

    /// <summary>Classifies and hides one effect; records it. <paramref name="viaInstance"/>: the creation hooks hide
    /// through the instance (it may never be listed).</summary>
    private bool TryHide(object fx, long uid, bool viaInstance, object? mgr)
    {
        var ctx = ReadContext(fx);
        var owner = _classify(ctx.Caster);
        OnClassified(uid, ctx, owner, viaInstance, fx);
        if (ctx.From == 0 && ctx.Belong == 0) OnZeroCaster(uid, fx);
        if (!_ledger.ShouldHide(uid, owner, _wanted, ctx.Visible)) return false;
        if (!(viaInstance || mgr is null ? SetInstanceVisible(fx, false) : SetManagerVisible(mgr, uid, false))) return false;
        _ledger.MarkHidden(uid, owner);
        if (viaInstance) _instances[uid] = fx;
        if (!viaInstance) OnSweepHidden(owner);   // the creation-hook path (viaInstance) isn't part of a sweep
        return true;
    }

    /// <summary>I1: drops `_instances` entries the ledger no longer holds — TakeReleasable (inside Release) can drop
    /// an ended uid from the ledger without ever showing it back, which previously left its wrapper here forever.</summary>
    private void PruneStaleInstances()
    {
        if (_instances.Count == 0) return;
        foreach (var uid in InstancePruneRule.NotInLedger(_instances.Keys, _ledger.Contains))
            _instances.Remove(uid);
    }

    /// <summary>I1: bounded prune for the creation-hook path, where Apply may not run again for a long time while the
    /// wanted set stays unchanged (so <see cref="PruneStaleInstances"/> never fires). Reuses <see cref="ReleaseState"/>
    /// (and so <see cref="EffectReleaseRule"/>) with an empty "listed" set to ask purely about the held instance.
    /// Follow-up: gated on <see cref="_pruneAt"/> rather than the fixed <see cref="MaxInstances"/>, and <see cref="_pruneAt"/>
    /// grows after every pass — a steady-state raid with hundreds of live entries pays the O(n) pass once, not on
    /// every hook call.</summary>
    private void PruneInstancesOverBudget()
    {
        if (_instances.Count <= _pruneAt) return;
        foreach (var uid in InstancePruneRule.OverBudget(_instances.Keys, _instances.Count, _pruneAt, u => ReleaseState(EmptyListed, u)))
        {
            _instances.Remove(uid);
            _ledger.Remove(uid);
        }
        _pruneAt = InstancePruneRule.NextThreshold(_instances.Count, MaxInstances);
    }

    private bool IsAlive(HashSet<long> listed, long uid) =>
        ReleaseState(listed, uid) is EffectReleaseState.Manager or EffectReleaseState.Instance;

    private bool TryShow(object mgr, HashSet<long> listed, long uid)
    {
        var ok = ReleaseState(listed, uid) switch
        {
            EffectReleaseState.Manager => SetManagerVisible(mgr, uid, true),
            EffectReleaseState.Instance => _instances.TryGetValue(uid, out var fx) && SetInstanceVisible(fx, true),
            _ => false,
        };
        _instances.Remove(uid);
        return ok;
    }

    /// <summary>Resolves the pure release rule for one recorded uid. ZEffect is pooled, so a held instance is only
    /// trusted when its current uid readback still matches (read inside try/catch — any exception means ended).</summary>
    private EffectReleaseState ReleaseState(HashSet<long> listed, long uid)
    {
        if (!_instances.TryGetValue(uid, out var fx)) return EffectReleaseRule.Resolve(listed, uid, null, false);
        if (IsDestroyed(fx)) return EffectReleaseRule.Resolve(listed, uid, null, true);
        long? readback;
        try { readback = ReadUid(fx); } catch { readback = null; }
        return EffectReleaseRule.Resolve(listed, uid, readback, false);
    }

    /// <summary>Creation hooks (AddEffectDisplay(ZEffect) / ZEffect.Init): hide a new effect while an effect layer is
    /// wanted. <paramref name="hook"/> is which one fired ("Init" or "AddEffectDisplay" — follow-up: so the
    /// born-hidden diagnostic can tell apart "invisible at Init, visible by AddEffectDisplay" from a genuine pooled
    /// born-hidden effect). I2(c): <see cref="OnEffectSeen"/> runs BEFORE the early return so the born-hidden
    /// diagnostic covers pool reuse even when no effect layer is currently wanted.</summary>
    private void OnEffectCreated(object? fx, string hook)
    {
        OnEffectSeen(fx, hook);
        if (_wanted == VisibilityLayers.None || fx is null || Environment.CurrentManagedThreadId != _mainThread || !IsEffect(fx)) return;
        try
        {
            var uid = ReadUid(fx);
            if (uid != 0) TryHide(fx, uid, viaInstance: true, mgr: null);
            PruneInstancesOverBudget();
        }
        catch (Exception ex) { WarnOnce("fxnew", "could not hide a new effect: " + (ex.InnerException ?? ex).Message); }
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) _log.Warning(Tag + message);
    }

    partial void OnClassified(long uid, (long Caster, long From, long Belong, bool Visible) ctx, VisibilityLayers owner, bool viaInstance, object fx);
    partial void OnZeroCaster(long uid, object fx);
    partial void OnEffectSeen(object? fx, string hook);
    partial void OnSwept(VisibilityLayers wanted, int hidden, int shown, int held);
    partial void OnSweepHidden(VisibilityLayers owner);
}
