using System;
using System.Reflection;
using HarmonyLib;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-facing helpers for <see cref="WorldNtfStubDispatcher"/>. The key
/// perf property lives in <see cref="StubCallAccessors"/>: the <c>GetUuid</c> /
/// <c>GetMethodId</c> / <c>GetCallData</c> accessors are resolved ONCE per
/// concrete stub <see cref="Type"/> and compiled into typed delegates, so the
/// per-packet path is a plain call — no <c>MethodInfo.Invoke</c> (object[] +
/// boxing), no <c>Type.GetMethod</c>. The payload span coercion is shared with
/// the other probes via <see cref="Il2CppSpanCoercion"/> (the projected
/// <c>get_Item</c> indexer returns the 0x40 sentinel under HarmonyX boxing, so
/// <c>ToArray()</c> is required).
///
/// Runs on the game's MAIN thread (inside <c>ZRpcImpl.ProcessFrameDownPackets</c>):
/// any reflection or marshalling failure is caught and translates to a
/// <c>false</c> / <c>null</c> return so the postfix can swallow the packet silently.
/// </summary>
internal sealed partial class WorldNtfStubDispatcher
{
    private StubCallAccessors? _accessors;
    private StubCallAccessors Accessors => _accessors ??= new StubCallAccessors(_log, "[StubDispatch]");

    // Read (uuid, methodId) off IStubCall via the per-type compiled accessors.
    // False on any failure — caller skips silently.
    private bool TryReadHeader(object stubCall, out ulong uuid, out uint methodId)
        => Accessors.TryReadHeader(stubCall, out uuid, out methodId);

    // Fetch IStubCall.GetCallData() via the cached accessor and coerce the
    // IL2CPP-projected ReadOnlySpan<byte> into a managed byte[] using the
    // shared Il2CppSpanCoercion helper. Returns null on any failure.
    private byte[]? ExtractPayload(object stubCall)
    {
        var callDataRaw = Accessors.GetCallData(stubCall);
        return callDataRaw is null ? null : CoerceCallData(callDataRaw);
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
        try { rawToArr = Il2CppSpanCoercion.InvokeToArray(callDataRaw); }
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
