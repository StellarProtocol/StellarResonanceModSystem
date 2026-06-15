using System;
using System.Reflection;
using HarmonyLib;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-facing helpers for <see cref="WorldNtfStubDispatcher"/>. The key
/// perf property lives here: the <c>GetUuid</c> / <c>GetMethodId</c> /
/// <c>GetCallData</c> <see cref="MethodInfo"/> accessors are resolved ONCE per
/// concrete stub <see cref="Type"/> and cached, so the per-packet path only
/// pays <c>MethodInfo.Invoke</c> — never a <c>Type.GetMethod</c> lookup. The
/// payload span coercion is shared with the other probes via
/// <see cref="Il2CppSpanCoercion"/> (the projected <c>get_Item</c> indexer
/// returns the 0x40 sentinel under HarmonyX boxing, so <c>ToArray()</c> is
/// required).
///
/// All helpers are I/O-thread safe: any reflection or marshalling failure is
/// caught and translates to a <c>false</c> / <c>null</c> return so the postfix
/// can swallow the packet silently.
/// </summary>
internal sealed partial class WorldNtfStubDispatcher
{
    // Accessors resolved at most once per concrete stub type. Re-resolve only
    // when a packet carries a different concrete type than last seen.
    private MethodInfo? _getUuid;
    private MethodInfo? _getMethodId;
    private MethodInfo? _getCallData;
    private Type? _resolvedFor;

    // Read (uuid, methodId) off IStubCall using accessors cached per concrete
    // type. Returns false on any reflection failure or null accessor — caller
    // skips silently. NOTE: the per-packet path here does NO Type.GetMethod
    // once _resolvedFor matches; it only Invokes the cached MethodInfos.
    private bool TryReadHeader(object stubCall, out ulong uuid, out uint methodId)
    {
        uuid = 0;
        methodId = 0;
        try
        {
            var t = stubCall.GetType();
            if (!ReferenceEquals(t, _resolvedFor))
                ResolveAccessors(t);

            if (_getUuid is null || _getMethodId is null) return false;

            var u = _getUuid.Invoke(stubCall, null);
            var m = _getMethodId.Invoke(stubCall, null);
            if (u is null || m is null) return false;

            uuid = Convert.ToUInt64(u);
            methodId = Convert.ToUInt32(m);
            return true;
        }
        catch
        {
            // Reflection failure on the I/O thread — swallow; the next packet
            // may have a different concrete type that succeeds.
            return false;
        }
    }

    // One-shot accessor resolution for a concrete stub type. Caches the three
    // header accessors and records the type so subsequent packets of the same
    // type skip the GetMethod lookups entirely.
    private void ResolveAccessors(Type t)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        _getUuid = t.GetMethod("GetUuid", flags);
        _getMethodId = t.GetMethod("GetMethodId", flags);
        _getCallData = t.GetMethod("GetCallData", flags);
        _resolvedFor = t;
    }

    // Fetch IStubCall.GetCallData() via the cached accessor and coerce the
    // IL2CPP-projected ReadOnlySpan<byte> into a managed byte[] using the
    // shared Il2CppSpanCoercion helper. Returns null on any failure.
    private byte[]? ExtractPayload(object stubCall)
    {
        var getCallData = _getCallData;
        if (getCallData is null) return null;

        object? callDataRaw;
        try { callDataRaw = getCallData.Invoke(stubCall, null); }
        catch { return null; }
        if (callDataRaw is null) return null;

        return CoerceCallData(callDataRaw);
    }

    // Resolve (once per process) and apply the shared ToArray span extractor.
    // Mirrors PandaCombatStubProbe.ExtractStubPayloadBytes — the projected
    // get_Item indexer is broken under HarmonyX boxing, so ToArray() is the
    // only correct path to the real bytes.
    private byte[]? CoerceCallData(object callDataRaw)
    {
        if (Il2CppSpanCoercion.SpanToArrayMethod is null
            && System.Threading.Interlocked.CompareExchange(
                ref Il2CppSpanCoercion.SpanExtractorResolved, 1, 0) == 0)
        {
            Il2CppSpanCoercion.ResolveSpanExtractor(_log, callDataRaw.GetType());
        }

        var toArr = Il2CppSpanCoercion.SpanToArrayMethod;
        if (toArr is null)
        {
            if (!_getCallDataFailLogged)
            {
                _getCallDataFailLogged = true;
                _log.Warning($"[StubDispatch] no ToArray extractor for {callDataRaw.GetType().FullName}; dispatch disabled");
            }
            return null;
        }

        object? rawToArr;
        try { rawToArr = toArr.Invoke(callDataRaw, null); }
        catch (Exception ex)
        {
            if (!_getCallDataFailLogged)
            {
                _getCallDataFailLogged = true;
                _log.Warning($"[StubDispatch] ToArray() threw: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }
        if (rawToArr is null) return null;

        return Il2CppSpanCoercion.CoerceToByteArray(rawToArr);
    }

    // Locate Zservice.WorldNtfStub.OnCallStub(IStubCall) via reflection. Same
    // approach as PandaCombatStubProbe: resolve type via Harmony's
    // AccessTools, then match the single OnCallStub overload by name +
    // parameter count = 1. Returns null + logs on every failure path so
    // Install can't take down the host.
    private MethodInfo? ResolveCallStubMethod()
    {
        Type? stubType;
        try
        {
            stubType = AccessTools.TypeByName(TargetTypeName);
        }
        catch (Exception ex)
        {
            _log.Warning($"[StubDispatch] AccessTools.TypeByName({TargetTypeName}) threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        if (stubType is null)
        {
            _log.Warning($"[StubDispatch] type {TargetTypeName} not found in any loaded assembly; not installed");
            return null;
        }

        return FindOnCallStubOverload(stubType);
    }

    // Walk declared instance methods looking for the OnCallStub(IStubCall)
    // overload. Match by name + parameter count = 1 — the stub class has no
    // other OnCallStub overload, so we don't bind to the exact IStubCall
    // interop type here.
    private MethodInfo? FindOnCallStubOverload(Type stubType)
    {
        MethodInfo[] methods;
        try
        {
            methods = stubType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }
        catch (Exception ex)
        {
            _log.Warning($"[StubDispatch] GetMethods({stubType.FullName}) threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        foreach (var m in methods)
        {
            if (m.Name != TargetMethodName) continue;
            try { if (m.GetParameters().Length == 1) return m; }
            catch { /* skip metadata edge cases */ }
        }

        _log.Warning($"[StubDispatch] {TargetTypeName}.{TargetMethodName}(IStubCall) not found; not installed");
        return null;
    }
}
