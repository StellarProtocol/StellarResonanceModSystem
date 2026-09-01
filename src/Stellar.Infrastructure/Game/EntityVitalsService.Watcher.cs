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
///
/// <para>C3b review fix: the callback is bound PER TRACKED ID via a small closure object
/// (<see cref="DirtyTrampoline"/>), not one shared instance method — so a change on one boss no longer
/// marks every OTHER tracked id dirty too. This sidesteps needing a <c>ZEntity</c>→uuid accessor (none
/// confirmed in the recon'd surface, §2.3) entirely: the uuid is already known at bind time
/// (<see cref="TrackForWatcher"/>'s own parameter), so it's captured in the trampoline instead of
/// extracted from the callback's <c>ZEntity</c> argument.</para>
/// </summary>
internal sealed partial class EntityVitalsService
{
    // Keeps each bound uuid's trampoline reachable for the lifetime of its watcher registration —
    // belt-and-braces against GC (Delegate.Target already holds a strong reference while the native
    // side retains the delegate, but an explicit owner-side reference removes any doubt) and gives
    // Untrack/Reset a place to drop it.
    private readonly System.Collections.Generic.Dictionary<long, object> _trampolines = new();

    // Closure object: captures (owner, uuid) so the watcher callback can mark exactly ITS uuid dirty
    // without needing to read a uuid back off the callback's ZEntity argument. MarkDirty is a plain
    // locked HashSet.Add — safe to call from whatever thread the game's attr-dispatch fires on.
    private sealed class DirtyTrampoline
    {
        private readonly EntityVitalsService _owner;
        private readonly long _uuid;
        public DirtyTrampoline(EntityVitalsService owner, long uuid) { _owner = owner; _uuid = uuid; }
        // Signature matches whatever BindEntityLuaAttrWatcher's callback delegate needs — one
        // reference-type parameter (the changed ZEntity), any return. The value is never used.
        public void OnFire(object entity) => _owner.MarkDirty(_uuid);
    }

    private void MarkDirty(long uuid)
    {
        lock (_cacheLock) { _dirty.Add(uuid); }
    }

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
            var trampoline = new DirtyTrampoline(this, uuid);
            var callback = BuildDirtyCallback(callbackParam[2].ParameterType, trampoline);
            if (callback is null) return;

            var attrIds = new uint[] { (uint)AttrTypeIds.AttrHp, (uint)AttrTypeIds.AttrMaxHp, (uint)AttrTypeIds.AttrMaxHpTotal };
            var token = _bindWatcherMethod.Invoke(mgr, new object[] { uuid, attrIds, callback });
            if (token is uint t)
            {
                lock (_cacheLock)
                {
                    _watcherTokens[uuid] = t;
                    _trampolines[uuid] = trampoline;
                }
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
            _trampolines.Remove(uuid);
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

    // Builds the watcher callback bound to trampoline.OnFire, targeting whatever concrete delegate
    // type BindEntityLuaAttrWatcher's 3rd parameter resolves to at runtime (an IL2CppInterop-generated
    // Action-shaped delegate over ZEntity — ZEntity's concrete Type is only known at runtime, a
    // hot-update assembly type, so this can't be written as ordinary compile-time C#). Relies on .NET's
    // contravariant delegate binding: a target method parameter typed `object` satisfies a delegate
    // expecting a more-derived reference-type parameter. Returns null on any failure.
    private static object? BuildDirtyCallback(Type delegateType, DirtyTrampoline trampoline)
    {
        try
        {
            var method = typeof(DirtyTrampoline).GetMethod(nameof(DirtyTrampoline.OnFire), AnyInstance);
            if (method is null) return null;
            return Delegate.CreateDelegate(delegateType, trampoline, method, throwOnBindFailure: false);
        }
        catch { return null; }
    }
}
