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
        if (_wanted == VisibilityLayers.None && _ledger.Count == 0) return VisibilityLayers.None;
        if (!Resolve() || Manager() is not { } mgr) { if (_wanted != 0) WarnOnce("fx", "effect hiding unavailable on this client"); return VisibilityLayers.None; }
        var listed = ListUids(mgr);
        var shown = 0;
        foreach (var uid in _ledger.TakeReleasable(_wanted, uid => IsAlive(listed, uid)))
            if (Show(mgr, listed, uid)) shown++;
        var hidden = _wanted == VisibilityLayers.None ? 0 : Sweep(mgr, listed);
        OnSwept(_wanted, hidden, shown, _ledger.Count);
        return _wanted;
    }

    private int Sweep(object mgr, HashSet<long>? listed)
    {
        if (listed is null) return 0;
        var hidden = 0;
        foreach (var uid in listed)
            if (Effect(mgr, uid) is { } fx && TryHide(fx, uid, viaInstance: false, mgr)) hidden++;
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
        return true;
    }

    private bool IsAlive(HashSet<long>? listed, long uid) =>
        listed?.Contains(uid) == true || (_instances.TryGetValue(uid, out var fx) && !IsDestroyed(fx));

    private bool Show(object mgr, HashSet<long>? listed, long uid)
    {
        var ok = listed?.Contains(uid) == true
            ? SetManagerVisible(mgr, uid, true)
            : _instances.TryGetValue(uid, out var fx) && SetInstanceVisible(fx, true);
        _instances.Remove(uid);
        return ok;
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
}
