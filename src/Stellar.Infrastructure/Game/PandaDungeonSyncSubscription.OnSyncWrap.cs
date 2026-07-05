using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Stellar.Infrastructure.Events;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// OnSync delegate-wrap route for <see cref="PandaDungeonSyncSubscription"/>.
///
/// <para>
/// <c>Panda.ZGame.DungeonSyncService.OnSync</c> is a public settable
/// <c>System.Action&lt;IntPtr, int&gt;</c> property that LUA assigns at its own
/// init (<c>lua/sync/dungeon_sync.lua</c>: <c>OnSync = function(data, count)</c>
/// → <c>ContainerMgr.DungeonSyncData:MergeData</c>). It is a designed runtime
/// extension point — swapping the delegate is a plain property write, no
/// HarmonyX, no trampolines over HybridCLR-interpreted code. Both parameters
/// are blittable, so Il2CppInterop's <c>DelegateSupport.ConvertDelegate</c>
/// handles the managed wrapper (unlike the event struct, which it rejects).
/// The wrapper copies the delta bytes, enqueues into the probe's deferred
/// queue, and ALWAYS calls the original — the game's own lua merge is never
/// skipped, even if our capture throws.
/// </para>
/// </summary>
internal sealed partial class PandaDungeonSyncSubscription
{
    private const string ServiceTypeFullName = "Panda.ZGame.DungeonSyncService";

    private object? _wrapService;            // resolved DungeonSyncService instance
    private object? _installedOnSync;        // the interop delegate WE set
    private object? _originalOnSync;         // lua's delegate at wrap time (call-through target)
    private MethodInfo? _onSyncGetter;
    private MethodInfo? _onSyncSetter;
    private MethodInfo? _originalInvoke;
    private Delegate? _managedWrapper;       // GC anchor for the converted wrapper

    /// <summary>
    /// Install (or re-install after a lua re-assign) the OnSync wrap. True while
    /// a wrap is in place — callers treat that as "route live" and skip MessagePipe.
    /// </summary>
    private bool TryWrapOnSync(MessagePipeContainerBridge bridge)
    {
        try
        {
            var service = _wrapService ??= bridge.TryResolveService(ServiceTypeFullName, out _);
            if (service is null) return false;

            if (!ResolveOnSyncAccessors(service)) return false;

            var current = _onSyncGetter!.Invoke(service, null);
            if (current is null) return false;                       // lua not initialized yet
            if (IsOurInstalledDelegate(current)) return true;        // wrap still in place

            // Fresh wrap (first install, or lua re-assigned a new handler over ours).
            _originalOnSync = current;
            _originalInvoke = current.GetType().GetMethod("Invoke");
            if (_originalInvoke is null) return false;

            var converted = ConvertWrapperTo(_onSyncGetter.ReturnType);
            if (converted is null) return false;

            _onSyncSetter!.Invoke(service, new[] { converted });
            _installedOnSync = _onSyncGetter.Invoke(service, null);  // re-read for pointer identity
            DiagOnSyncWrapped();
            return true;
        }
        catch
        {
            // A scene change disposes the child scope — the cached service/interop
            // handles go stale and reads throw. Reset and re-resolve on later ticks.
            _wrapService = null;
            _installedOnSync = null;
            _originalOnSync = null;
            return false;
        }
    }

    private bool ResolveOnSyncAccessors(object service)
    {
        if (_onSyncGetter is not null && _onSyncSetter is not null) return true;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var prop = service.GetType().GetProperty("OnSync", flags);
        _onSyncGetter = prop?.GetGetMethod(true);
        _onSyncSetter = prop?.GetSetMethod(true);
        if (_onSyncGetter is null || _onSyncSetter is null)
        {
            DiagOnSyncShapeMissing(service.GetType().FullName);
            return false;
        }
        return true;
    }

    // Interop wrappers are re-created per property read, so reference equality
    // lies — compare the underlying native pointers when both sides expose one
    // (Il2CppObjectBase.Pointer); fall back to reference equality for managed.
    private bool IsOurInstalledDelegate(object current)
    {
        if (_installedOnSync is null) return false;
        if (ReferenceEquals(current, _installedOnSync)) return true;
        var pProp = current.GetType().GetProperty("Pointer");
        var qProp = _installedOnSync.GetType().GetProperty("Pointer");
        if (pProp is null || qProp is null) return false;
        return Equals(pProp.GetValue(current), qProp.GetValue(_installedOnSync));
    }

    // Convert the managed wrapper to the property's declared delegate type: direct
    // assignment when it's a managed Action<IntPtr,int>; DelegateSupport.ConvertDelegate
    // (via reflection — blittable params, supported) when it's the interop projection.
    private object? ConvertWrapperTo(Type declaredType)
    {
        Action<IntPtr, int> wrapper = OnSyncWrapper;
        _managedWrapper = wrapper;
        if (declaredType.IsAssignableFrom(wrapper.GetType())) return wrapper;

        var support = Type.GetType("Il2CppInterop.Runtime.DelegateSupport, Il2CppInterop.Runtime");
        var convert = support?.GetMethod("ConvertDelegate", BindingFlags.Public | BindingFlags.Static);
        if (convert is null)
        {
            DiagOnSyncShapeMissing($"DelegateSupport missing for {declaredType.FullName}");
            return null;
        }
        return convert.MakeGenericMethod(declaredType).Invoke(null, new object[] { wrapper });
    }

    // The wrap body: copy + enqueue, then ALWAYS the original. Runs wherever the
    // game invokes OnSync (its own MessagePipe handler) — capture must never
    // throw into that path, and the lua merge must run no matter what we do.
    private void OnSyncWrapper(IntPtr data, int count)
    {
        try
        {
            if (data != IntPtr.Zero && count > 0 && count <= 4_194_304)
            {
                var copy = new byte[count];
                Marshal.Copy(data, copy, 0, count);
                _probe.OnDungeonSyncDeltaDeferred(copy);
                DiagDeltaCaptured(count);
            }
        }
        catch { /* capture is best-effort */ }
        finally
        {
            try { _originalInvoke?.Invoke(_originalOnSync, new object[] { data, count }); }
            catch { /* the game's own handler faulting is not ours to swallow silently — but never rethrow */ }
        }
    }

    /// <summary>Restore lua's original OnSync delegate (called from Dispose).</summary>
    private void UnwrapOnSync()
    {
        try
        {
            if (_wrapService is null || _onSyncSetter is null || _originalOnSync is null) return;
            var current = _onSyncGetter?.Invoke(_wrapService, null);
            if (current is not null && IsOurInstalledDelegate(current))
                _onSyncSetter.Invoke(_wrapService, new[] { _originalOnSync });
        }
        catch { /* shutdown path — never throw */ }
        finally
        {
            _installedOnSync = null;
            _originalOnSync = null;
            _managedWrapper = null;
        }
    }
}
