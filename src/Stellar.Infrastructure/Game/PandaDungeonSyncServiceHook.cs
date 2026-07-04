using System;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// HarmonyX hook on <c>Panda.ZGame.DungeonSyncService</c> (Panda.Script) — the
/// C# DI service the dungeon container's dirty-DELTA updates flow through on
/// their way to Lua. Confirmed trace (StarResonanceData lua):
/// <c>lua/sync/dungeon_sync.lua</c> assigns
/// <c>Z.DIServiceMgr.DungeonSyncService.OnSync = function(data, count)</c> which
/// hands the raw bytes to
/// <c>Z.ContainerMgr.DungeonSyncData:MergeData</c>
/// (<c>lua/zcontainer/dungeon_sync_data.lua</c> → field 15 timerInfo →
/// <c>lua/zcontainer/dungeon_timer_info.lua</c> field 2 <c>startTime</c>) — the
/// value the game's own dungeon-timer HUD displays. The service's ctor
/// subscribes MessagePipe <c>ISubscriber&lt;SyncDungeonDirtyDataMessageEvent&gt;</c>;
/// the compiler-generated handler (<c>&lt;.ctor&gt;b__4_0</c>, interop-projected
/// as <c>__ctor_b__4_0(SyncDungeonDirtyDataMessageEvent)</c>) is the hookable
/// seam where the delta buffer is available:
/// <c>event.VData</c> (<c>Zproto.BufferStream</c>) <c>.Buffer</c>
/// (<c>Google.Protobuf.ByteString</c>) holds the BARE container-merge blob.
///
/// <para>
/// The PREFIX (buffer guaranteed live before the original runs — BufferStream /
/// ByteString are pooled and may be recycled after publish) copies the bytes to
/// a managed <c>byte[]</c> immediately and enqueues into
/// <see cref="PandaDungeonProbe"/>'s crash-safe deferred queue
/// (<see cref="PandaDungeonProbe.OnDungeonSyncDeltaDeferred"/>); parsing, sink
/// writes and logging all happen at drain on the gated framework tick. This
/// hook sits DOWNSTREAM of whatever wire method delivers the delta, so it
/// catches the true <c>timerInfo.startTime</c> regardless of whether the
/// inferred method-24 stub tap (kept as corroborating diagnostics) ever fires.
/// The handler method is resolved by its parameter TYPE (name-mangling-proof),
/// and every failure path logs + degrades — a missing seam never takes down
/// the host. Never throws across the IL2CPP boundary.
/// </para>
/// </summary>
internal sealed partial class PandaDungeonSyncServiceHook
{
    private const string ServiceTypeName = "Panda.ZGame.DungeonSyncService";
    private const string EventParamTypeName = "SyncDungeonDirtyDataMessageEvent";

    private static PandaDungeonSyncServiceHook? Instance;

    private readonly PandaDungeonProbe _probe;
    private readonly IPluginLog _log;
    private Harmony? _harmony;
    private bool _patched;

    public PandaDungeonSyncServiceHook(PandaDungeonProbe probe, IPluginLog log)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _log   = log   ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Resolve <c>DungeonSyncService</c>'s MessagePipe handler and install the
    /// PREFIX. Idempotent; every failure logs a warning and leaves the rest of
    /// the framework untouched (the method-24 stub tap remains as fallback
    /// diagnostics).
    /// </summary>
    public void PatchAll(string harmonyId)
    {
        if (_patched) return;
        _patched = true;

        var serviceType = ResolveServiceType();
        if (serviceType is null)
        {
            _log.Warning($"[DungeonSyncHook] {ServiceTypeName} not found in any loaded assembly; hook not installed");
            return;
        }

        var handler = ResolveHandlerMethod(serviceType);
        if (handler is null) return;

        var prefix = typeof(PandaDungeonSyncServiceHook).GetMethod(
            nameof(OnDirtyDataMessagePrefix), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            _log.Warning("[DungeonSyncHook] OnDirtyDataMessagePrefix method missing (build error)");
            return;
        }

        try
        {
            Instance = this;
            _harmony = new Harmony(harmonyId);
            _harmony.Patch(handler, prefix: new HarmonyMethod(prefix));
            _log.Info($"[DungeonSyncHook] patched {serviceType.FullName}.{handler.Name}({EventParamTypeName}) (PREFIX)");
        }
        catch (Exception ex)
        {
            Instance = null;
            _log.Warning($"[DungeonSyncHook] failed to patch {serviceType.FullName}.{handler.Name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Type? ResolveServiceType()
    {
        Type? serviceType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { serviceType ??= asm.GetType(ServiceTypeName, throwOnError: false); }
            catch { /* skip unloadable assembly */ }
            if (serviceType is not null) break;
        }
        return serviceType;
    }

    // Walk declared instance methods for the MessagePipe subscription callback:
    // exactly one parameter whose TYPE is SyncDungeonDirtyDataMessageEvent.
    // Matching by parameter type instead of name survives the interop mangling
    // of the compiler-generated "<.ctor>b__4_0" handler name.
    private MethodInfo? ResolveHandlerMethod(Type serviceType)
    {
        MethodInfo[] methods;
        try
        {
            methods = serviceType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }
        catch (Exception ex)
        {
            _log.Warning($"[DungeonSyncHook] GetMethods({serviceType.FullName}) threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        foreach (var m in methods)
        {
            ParameterInfo[] ps;
            try { ps = m.GetParameters(); }
            catch { continue; }
            if (ps.Length != 1) continue;
            if (ps[0].ParameterType?.Name != EventParamTypeName) continue;
            return m;
        }

        _log.Warning($"[DungeonSyncHook] no {serviceType.FullName} method taking {EventParamTypeName} found; hook not installed");
        return null;
    }

    // Harmony PREFIX for DungeonSyncService's SyncDungeonDirtyDataMessageEvent
    // handler. __0 = the (interop-projected) event argument, matched by position
    // so the mangled parameter name is irrelevant. Catch-all: never throws
    // across the IL2CPP boundary; runs on the MessagePipe publish thread
    // (downstream of the network receive), so it must stay enqueue-only.
    private static void OnDirtyDataMessagePrefix(object __0)
    {
        try { Instance?.CaptureDelta(__0); }
        catch { /* never throw into the game's dispatch */ }
    }

    // Copy the delta bytes out of the (pooled) event payload IMMEDIATELY, then
    // hand the managed copy to the probe's deferred queue. No parsing, no sink
    // writes here — only the one-shot diagnostics.
    private void CaptureDelta(object? evt)
    {
        if (evt is null) return;

        var blob = ExtractBlob(evt);
        if (blob is null)
        {
            DiagExtractFailed();
            return;
        }

        _probe.OnDungeonSyncDeltaDeferred(blob);
        DiagDeltaCaptured(blob.Length);
    }
}
