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

    public VisibilityLayers Apply(VisibilityLayers requested)
    {
        _wanted = requested & VisibilityLayerSets.Effects;
        if (_unavailable) return VisibilityLayers.None;   // already known dead on this client — don't re-resolve every call
        if (_wanted == VisibilityLayers.None && _ledger.Count == 0) return VisibilityLayers.None;
        if (!Resolve() || Manager() is not { } mgr) { if (_wanted != 0) WarnOnce("fx", "effect hiding unavailable on this client"); return VisibilityLayers.None; }
        var listed = ListUids(mgr);
        var shown = listed is null ? 0 : Release(mgr, listed);
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
            try { if (Effect(mgr, uid) is { } fx && TryHide(fx, uid, viaInstance: false, mgr)) hidden++; }
            catch (Exception ex) { WarnOnce("fxsweep", "effect sweep failed for uid " + uid + ": " + (ex.InnerException ?? ex).Message); }
        }
        return hidden;
    }

    /// <summary>Classifies and hides one effect; records it. <paramref name="viaInstance"/>: the creation hooks hide
    /// through the instance (it may never be listed).</summary>
    private bool TryHide(object fx, long uid, bool viaInstance, object? mgr)
    {
        var (caster, from, belong, visible) = ReadContext(fx);
        var owner = _classify(caster);
        OnClassified(uid, from, belong, owner);
        if (!_ledger.ShouldHide(uid, owner, _wanted, visible)) return false;
        if (!(viaInstance || mgr is null ? SetInstanceVisible(fx, false) : SetManagerVisible(mgr, uid, false))) return false;
        _ledger.MarkHidden(uid, owner);
        if (viaInstance) _instances[uid] = fx;
        if (!viaInstance) OnSweepHidden(owner);   // the creation-hook path (viaInstance) isn't part of a sweep
        return true;
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

    /// <summary>Creation hooks (AddEffectDisplay(ZEffect) / ZEffect.Init): hide a new effect while an effect layer is wanted.</summary>
    private void OnEffectCreated(object? fx)
    {
        if (_wanted == VisibilityLayers.None || fx is null || Environment.CurrentManagedThreadId != _mainThread || !IsEffect(fx)) return;
        try
        {
            var uid = ReadUid(fx);
            if (uid != 0) TryHide(fx, uid, viaInstance: true, mgr: null);
        }
        catch (Exception ex) { WarnOnce("fxnew", "could not hide a new effect: " + (ex.InnerException ?? ex).Message); }
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) _log.Warning(Tag + message);
    }

    partial void OnClassified(long uid, long from, long belong, VisibilityLayers owner);
    partial void OnSwept(VisibilityLayers wanted, int hidden, int shown, int held);
    partial void OnSweepHidden(VisibilityLayers owner);
}
