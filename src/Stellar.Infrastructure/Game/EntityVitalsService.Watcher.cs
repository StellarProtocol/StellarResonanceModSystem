using System;
using System.Reflection;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Event-driven watcher plumbing for <see cref="EntityVitalsService"/> — the no-polling half of
/// decision 2 (2026-08-26 raid-bosshp-capture-design). Binds
/// <c>ZEntityMgr.BindEntityLuaAttrWatcher</c> per tracked id so a real attr change marks it dirty for
/// <see cref="EntityVitalsService.Tick"/> to re-read, and releases via
/// <c>UnbindEntityLuaAttrWater</c> on untrack. Best-effort throughout: binding failure (or the method
/// simply not resolving) leaves the id unwatched, which only costs the push-driven refresh between
/// <see cref="EntityVitalsService.TryGetBlood"/> calls — <c>TryGetBlood</c> itself always attempts a
/// direct live read first and never depends on a bound watcher to answer correctly.
/// </summary>
internal sealed partial class EntityVitalsService
{
    private void TrackForWatcher(long uuid)
    {
        if (_bindWatcherMethod is null || _mgrInstanceProperty is null) return;
        lock (_cacheLock)
        {
            if (_watcherTokens.ContainsKey(uuid)) return;
        }
        try
        {
            var mgr = _mgrInstanceProperty.GetValue(null);
            if (mgr is null) return;
            var callbackParam = _bindWatcherMethod.GetParameters();
            if (callbackParam.Length != 3) return;
            var callback = BuildDirtyCallback(callbackParam[2].ParameterType);
            if (callback is null) return;

            var attrIds = new uint[] { (uint)AttrTypeIds.AttrHp, (uint)AttrTypeIds.AttrMaxHp, (uint)AttrTypeIds.AttrMaxHpTotal };
            var token = _bindWatcherMethod.Invoke(mgr, new object[] { uuid, attrIds, callback });
            if (token is uint t)
            {
                lock (_cacheLock) { _watcherTokens[uuid] = t; }
            }
        }
        catch
        {
            // Headless-unverifiable IL2Cpp delegate marshaling (docs/il2cpp-probing-safety.md) — this id
            // simply stays unwatched; TryGetBlood's per-call live read still answers correctly.
        }
    }

    private void Untrack(long uuid)
    {
        uint token;
        lock (_cacheLock)
        {
            _cache.Remove(uuid);
            _dirty.Remove(uuid);
            if (!_watcherTokens.TryGetValue(uuid, out token)) return;
            _watcherTokens.Remove(uuid);
        }
        if (_unbindWatcherMethod is null || _mgrInstanceProperty is null) return;
        try
        {
            var mgr = _mgrInstanceProperty.GetValue(null);
            if (mgr is null) return;
            _unbindWatcherMethod.Invoke(mgr, new object[] { uuid, token });
        }
        catch { /* best-effort cleanup */ }
    }

    // Builds the watcher callback bound to OnAnyAttrDirty, targeting whatever concrete delegate type
    // BindEntityLuaAttrWatcher's 3rd parameter resolves to at runtime (an IL2CppInterop-generated
    // Action-shaped delegate over ZEntity — ZEntity's concrete Type is only known at runtime, a
    // hot-update assembly type, so this can't be written as ordinary compile-time C#). Relies on .NET's
    // contravariant delegate binding: a target method parameter typed `object` satisfies a delegate
    // expecting a more-derived reference-type parameter. Returns null on any failure.
    private object? BuildDirtyCallback(Type delegateType)
    {
        try
        {
            var method = typeof(EntityVitalsService).GetMethod(nameof(OnAnyAttrDirty), AnyInstance);
            if (method is null) return null;
            return Delegate.CreateDelegate(delegateType, this, method, throwOnBindFailure: false);
        }
        catch { return null; }
    }

    // Watcher callback — fires when ANY watched attr changes for ANY bound uuid. The callback signature
    // carries the changed ZEntity, not a uuid, and there is no confirmed ZEntity->uuid accessor in the
    // recon'd surface (2026-08-26 recon §2.3) — rather than guess at one, this marks the WHOLE tracked
    // set dirty. Cheap: the tracked set is a handful of bosses/elites per raid, and Tick() only re-reads
    // ids actually in the dirty set.
    private void OnAnyAttrDirty(object entity)
    {
        lock (_cacheLock)
        {
            foreach (var uuid in _watcherTokens.Keys) _dirty.Add(uuid);
        }
    }
}
