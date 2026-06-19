using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Lua-bridge reflection-resolution + read path for <see cref="PandaLoadoutProbe"/>.
///
/// <para>Resolves the game's <b>tolua#</b> <c>LuaState</c> + <c>DoString</c> entry
/// point identically to <see cref="PandaModuleEquipProbe"/> (static property
/// <c>ZLuaFramework.LuaState.mainState</c> + <c>void DoString(string,string)</c>),
/// then runs the switch chunk
/// <c>Z.VMMgr.GetVM("&lt;vm&gt;").&lt;ApplyFn&gt;(projectId, token)</c> inside the
/// canonical <c>Z.CoroUtil.create_coro_xpcall(fn)()</c> wrapper (REQUIRED for any
/// async VM call that yields on an RPC reply — see
/// <see cref="PandaModuleEquipProbe.BuildEquipChunk"/>).</para>
///
/// <para>The current-id read (<see cref="ReadCurrentProfessionProjectId"/>) reaches a
/// live <c>Zproto.CurrentProfessionProjectIdInfoContainerArchive</c>
/// (<c>ZContainer&lt;CurrentProfessionProjectIdInfo&gt;</c>) by reflection — the same
/// archive shape <see cref="PandaInventoryPullReader"/> reads
/// (<c>Data__Original</c> / <c>GetDataRef()</c> expose the proto) — and returns the
/// <c>CurrentProfessionProjectId</c> int.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // chunkName passed to DoString — surfaces as the source label in any Lua
    // traceback the game logs, so a switch-chunk error is greppable.
    private const string ChunkName = "Stellar.LoadoutSwitch";

    // ── DISCOVERED-AT-RUNTIME identifiers ──────────────────────────────────────
    // recon/loadout-switch-findings.md F1/F3: the VM key, apply-function name, and
    // saved-project list getter live in compiled Lua and are pinned by the in-world
    // introspection diagnostic (PandaLoadoutProbe.Diagnostics.cs). Until pinned the
    // resolved flags stay false: CallApplyAsync → GameApiUnavailable, ReadLoadouts →
    // empty. The current-id read is independent and always available.
    //
    // UNCONFIRMED placeholders — these are the leading candidates from findings F1
    // (naming convention) and are filled with the CONFIRMED values once the in-world
    // introspection (RunIntrospectionIfDue) logs them. Do NOT enable apply until the
    // VM/function are observed in the BepInEx log.
    private const string ProfessionVmName = "profession";
    private const string ApplyFnName = "ChangeProfessionProject";

    // Flip true once the corresponding identifier is pinned + verified in-world.
    // static readonly (not const) so a not-yet-pinned (false) value does not make the
    // gated apply/list paths compile-time-unreachable (CS0162).
    private static readonly bool _applyFnResolved = false;
    private static readonly bool _listGetterResolved = false;

    // The mandatory cancelToken arg the game passes to async VM RPCs (mirror of
    // module-equip). NeverCancelToken is the game's own fire-and-forget token.
    private const string NeverCancelToken = "ZUtil.ZCancelSource.NeverCancelToken";

    private const string CurrentIdArchiveTypeName = "Zproto.CurrentProfessionProjectIdInfoContainerArchive";
    private const string CurrentIdFieldName = "CurrentProfessionProjectId";

    private volatile bool _bridgeResolved;
    private bool _resolutionFailureLogged;

    private MethodInfo? _mainStateGetter;   // static LuaState mainState { get; }
    private MethodInfo? _doString;          // void DoString(string chunk, string chunkName)

    private int _resolveTickCounter;
    private const int ResolveAttemptEveryTicks = 60;

    /// <summary>
    /// Proactively resolve the Lua bridge off the Update tick (throttled) so
    /// <see cref="PandaLoadoutProbe.IsResolved"/> / <c>ILoadout.IsAvailable</c> flips
    /// true WITHOUT requiring an apply dispatch. No-op once resolved.
    /// </summary>
    internal void TryResolveBridgeIfDue()
    {
        if (_bridgeResolved) return;
        if (_resolveTickCounter++ % ResolveAttemptEveryTicks != 0) return;
        EnsureBridgeResolved();
    }

    private bool EnsureBridgeResolved()
    {
        if (_bridgeResolved) return true;
        try { return TryResolveBridge(); }
        catch (Exception ex)
        {
            OnResolutionFailure($"bridge resolution threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool TryResolveBridge()
    {
        var luaStateType = _typeRegistry.FindType("ZLuaFramework.LuaState")
            ?? _typeRegistry.FindType("LuaInterface.LuaState")
            ?? FindTypeByShortName("LuaState");
        if (luaStateType is null)
        {
            OnResolutionFailure("ZLuaFramework.LuaState type not loaded yet");
            return false;
        }

        _mainStateGetter = luaStateType.GetProperty("mainState", AnyStatic)?.GetGetMethod(nonPublic: true);
        if (_mainStateGetter is null)
        {
            OnResolutionFailure("LuaState.mainState (static property) not found");
            return false;
        }

        _doString = FindDoString(luaStateType);
        if (_doString is null)
        {
            OnResolutionFailure("LuaState.DoString(string,string) not found");
            return false;
        }

        _bridgeResolved = true;
        OnResolutionSucceeded();
        return true;
    }

    private static MethodInfo? FindDoString(Type luaStateType)
    {
        foreach (var m in luaStateType.GetMethods(AnyInstance))
        {
            if (m.Name != "DoString" || m.IsGenericMethodDefinition) continue;
            if (m.ReturnType != typeof(void)) continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
            {
                return m;
            }
        }
        return null;
    }

    private object? GetMainLuaState()
    {
        if (_mainStateGetter is null) return null;
        try { return _mainStateGetter.Invoke(null, Array.Empty<object>()); }
        catch { return null; }
    }

    // Runs the switch chunk via DoString. Returns false on any marshalling failure
    // so the caller maps it to GameApiUnavailable; a Lua-side error (failed
    // pre-flight / refusal EErrorCode) is reported by the game's own xpcall handler
    // under ChunkName, not as a C# exception.
    private bool InvokeLuaDispatch(int projectId)
    {
        var state = GetMainLuaState();
        if (state is null)
        {
            OnResolutionFailure("LuaState.mainState returned null at dispatch");
            return false;
        }
        if (_doString is null) return false;

        var chunk = BuildSwitchChunk(ProfessionVmName, ApplyFnName, projectId);
        try
        {
            _doString.Invoke(state, new object[] { chunk, ChunkName });
            return true;
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            _log.Warning($"[Stellar][Loadout] Lua dispatch threw: {inner.GetType().Name}: {inner.Message} | chunk={chunk}");
            return false;
        }
    }

    // Builds the switch chunk mirroring the game's own loadout-dropdown row click:
    // (Z.VMMgr.GetVM("profession")).ChangeProfessionProject(projectId, token),
    // launched inside a child coroutine via Z.CoroUtil.create_coro_xpcall(fn)().
    // The coroutine is REQUIRED: the switch is an async server RPC that yields on
    // reply, and DoString runs on the bare main thread where yield throws. vmName/
    // func are internal constants and projectId is numeric — no external text is
    // interpolated, so there is no Lua-injection surface.
    internal static string BuildSwitchChunk(string vmName, string func, int projectId)
        => string.Format(
            CultureInfo.InvariantCulture,
            "(Z.CoroUtil.create_coro_xpcall(function() local vm=Z.VMMgr.GetVM(\"{0}\"); vm.{1}({2}, {3}) end))()",
            vmName, func, projectId, NeverCancelToken);

    // ── Saved-project list read (via the VM table) ─────────────────────────────
    // Reads Z.VMMgr.GetVM("profession").GetProjectInfoList() by resolving the VM
    // object + list getter through the Lua bridge's C# reflection surface. The VM
    // is a Lua table, so the only robust C# read is the current-id container
    // (below); the list itself is read from the cached C# model the VM exposes when
    // available, else falls back to the current id as a single entry. See
    // PandaLoadoutProbe.Diagnostics.cs RunIntrospectionIfDue for how the getter +
    // entry fields were pinned.
    private IReadOnlyList<LoadoutEntry> ReadLoadoutsViaVm()
    {
        var entries = ReadProjectListFromModel();
        if (entries is not null) return entries;

        // Fallback: surface at least the active loadout so the overlay/hotkeys have
        // a valid id to round-trip while the full list read is being finalised.
        var current = ReadCurrentProfessionProjectId();
        return current is { } id
            ? new[] { new LoadoutEntry(id, $"Loadout {id}") }
            : Array.Empty<LoadoutEntry>();
    }

    // Best-effort C# read of the saved-project list off the profession model
    // container, mirroring the current-id container hop. The list lives in a
    // ZContainer<...> whose proto carries a repeated project field; we reflect the
    // entries' Id/Name. Returns null if the container/shape isn't reachable (the
    // VM-table read is the authoritative source — see findings F3).
    private IReadOnlyList<LoadoutEntry>? ReadProjectListFromModel()
    {
        // The introspection diagnostic enumerates the VM members + any C# model
        // container holding the list. When pinned to a reflectable container this
        // method reads it; until then it returns null and the caller falls back to
        // the current-id single entry. Left intentionally model-agnostic: the
        // authoritative list comes from the VM's Lua table, surfaced for the plugin
        // layer in a later task. See findings F3.
        return null;
    }

    // ── Current-id read (CONFIRMED C#-reflectable, findings F3) ────────────────
    private FieldInfo? _currentIdField;
    private Func<object?>? _archiveProvider;
    private int _currentIdResolveTickCounter;

    /// <summary>
    /// Reads <c>CurrentProfessionProjectId</c> off the live
    /// <c>CurrentProfessionProjectIdInfoContainerArchive</c>. Returns null until the
    /// archive instance + field are reflectable (pre-login / pre-sync).
    /// </summary>
    internal int? ReadCurrentProfessionProjectId()
    {
        try
        {
            ResolveCurrentIdAccessorsIfDue();
            var archive = _archiveProvider?.Invoke();
            if (archive is null) return null;

            var proto = ReadArchiveProto(archive);
            if (proto is null) return null;

            EnsureCurrentIdField(proto.GetType());
            if (_currentIdField is null) return null;

            var value = _currentIdField.GetValue(proto);
            return value is int i ? i : null;
        }
        catch { return null; }
    }

    private void ResolveCurrentIdAccessorsIfDue()
    {
        // Already resolved — never scan again.
        if (_archiveProvider is not null) return;

        // CRITICAL perf gate: the archive only becomes reachable post-login/sync, so
        // we must keep retrying — but ResolveArchiveInstanceProvider / FindTypeByShortName
        // walk every loaded IL2CPP assembly via GetTypes(). Running that every Update
        // frame collapses FPS (~0.2). Throttle the scan to once every
        // ResolveAttemptEveryTicks calls, mirroring TryResolveBridgeIfDue.
        if (_currentIdResolveTickCounter++ % ResolveAttemptEveryTicks != 0) return;

        var archiveType = _typeRegistry.FindType(CurrentIdArchiveTypeName)
            ?? FindTypeByShortName("CurrentProfessionProjectIdInfoContainerArchive");
        if (archiveType is null) return;

        // The archive is a synced ZContainer held in the game's model tree. Reach a
        // live instance via the same static-holder / ZSingleton strategies the
        // inventory reader uses (Strategy A/B). Re-resolve each call until found.
        var provider = ResolveArchiveInstanceProvider(archiveType);
        if (provider is not null) _archiveProvider = provider;
    }

    // Walks loaded assemblies for any static field/property whose value type IS the
    // archive type (the model tree caches the container in a static-reachable slot
    // on this build). Mirrors PandaInventoryPullReader.ResolveStaticHolderOfType.
    private Func<object?>? ResolveArchiveInstanceProvider(Type archiveType)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string asmName;
            try { asmName = asm.GetName().Name ?? string.Empty; }
            catch { continue; }
            if (ShouldSkipAssemblyForScan(asmName)) continue;

            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types!; }
            catch { continue; }

            foreach (var t in types)
            {
                if (t is null) continue;
                var provider = FindArchiveHolderOn(t, archiveType);
                if (provider is not null) return provider;
            }
        }
        return null;
    }

    private static Func<object?>? FindArchiveHolderOn(Type t, Type archiveType)
    {
        PropertyInfo[] props;
        try { props = t.GetProperties(AnyStatic); } catch { props = Array.Empty<PropertyInfo>(); }
        foreach (var p in props)
        {
            Type ptype;
            try { ptype = p.PropertyType; } catch { continue; }
            if (!archiveType.IsAssignableFrom(ptype)) continue;
            var captured = p;
            return () => { try { return captured.GetValue(null); } catch { return null; } };
        }

        FieldInfo[] fields;
        try { fields = t.GetFields(AnyStatic); } catch { fields = Array.Empty<FieldInfo>(); }
        foreach (var f in fields)
        {
            Type ftype;
            try { ftype = f.FieldType; } catch { continue; }
            if (!archiveType.IsAssignableFrom(ftype)) continue;
            var captured = f;
            return () => { try { return captured.GetValue(null); } catch { return null; } };
        }
        return null;
    }

    // Pulls the CurrentProfessionProjectIdInfo proto out of the archive, mirroring
    // the inventory reader's archive access: prefer the Data__Original field, then a
    // GetDataRef()/Data getter.
    private static object? ReadArchiveProto(object archive)
    {
        var type = archive.GetType();

        var dataField = type.GetField("Data__Original", AnyInstance);
        if (dataField is not null)
        {
            try { var v = dataField.GetValue(archive); if (v is not null) return v; } catch { /* fall through */ }
        }

        var getDataRef = type.GetMethod("GetDataRef", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);
        if (getDataRef is not null)
        {
            try { var v = getDataRef.Invoke(archive, Array.Empty<object>()); if (v is not null) return v; } catch { /* fall through */ }
        }

        var dataProp = type.GetProperty("Data", AnyInstance);
        if (dataProp is not null)
        {
            try { return dataProp.GetValue(archive); } catch { /* fall through */ }
        }
        return null;
    }

    private void EnsureCurrentIdField(Type protoType)
    {
        if (_currentIdField is not null) return;
        _currentIdField = protoType.GetField(CurrentIdFieldName, AnyInstance)
            ?? protoType.GetField($"{CurrentIdFieldName}__Original", AnyInstance);
    }

    private static Type? FindTypeByShortName(string shortName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string asmName;
            try { asmName = asm.GetName().Name ?? string.Empty; }
            catch { continue; }
            if (ShouldSkipAssemblyForScan(asmName)) continue;

            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types!; }
            catch { continue; }
            foreach (var t in types)
            {
                if (t is null) continue;
                string name;
                try { name = t.Name; } catch { continue; }
                if (string.Equals(name, shortName, StringComparison.Ordinal)) return t;
            }
        }
        return null;
    }

    private static bool ShouldSkipAssemblyForScan(string asmName)
    {
        if (string.IsNullOrEmpty(asmName)) return false;
        if (asmName.StartsWith("UnityEngine", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("System", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("Microsoft", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("Il2Cpp", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("BepInEx", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("MonoMod", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("HarmonyX", StringComparison.Ordinal) || asmName == "0Harmony") return true;
        if (asmName.StartsWith("mscorlib", StringComparison.Ordinal) || asmName.StartsWith("netstandard", StringComparison.Ordinal)) return true;
        return false;
    }
}
