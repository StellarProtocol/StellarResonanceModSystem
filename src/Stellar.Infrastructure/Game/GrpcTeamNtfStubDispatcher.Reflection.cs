using System;
using System.Reflection;
using HarmonyLib;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-facing helpers for <see cref="GrpcTeamNtfStubDispatcher"/>. The
/// key perf property lives here: the <c>GetUuid</c> / <c>GetMethodId</c> /
/// <c>GetCallData</c> <see cref="MethodInfo"/> accessors are resolved ONCE per
/// concrete stub <see cref="Type"/> and cached, so the per-packet path only
/// pays <c>MethodInfo.Invoke</c> — never a <c>Type.GetMethod</c> lookup.
///
/// <para>
/// Mirrors <see cref="WorldNtfStubDispatcher"/>.<c>Reflection.cs</c> exactly:
/// shared <see cref="Il2CppSpanCoercion"/> for the <c>ToArray()</c> span
/// extraction; same per-type accessor cache pattern. All helpers are I/O-thread
/// safe; reflection or marshalling failures translate to a <c>false</c> /
/// <c>null</c> return so the postfix can swallow the packet silently.
/// </para>
/// </summary>
internal sealed partial class GrpcTeamNtfStubDispatcher
{
    // Accessors resolved at most once per concrete stub type.
    private MethodInfo? _getUuid;
    private MethodInfo? _getMethodId;
    private MethodInfo? _getCallData;
    private Type? _resolvedFor;

    // Read (uuid, methodId) off IStubCall using accessors cached per concrete
    // type. Returns false on any reflection failure or null accessor.
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
            return false;
        }
    }

    // One-shot accessor resolution for a concrete stub type.
    private void ResolveAccessors(Type t)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        _getUuid = t.GetMethod("GetUuid", flags);
        _getMethodId = t.GetMethod("GetMethodId", flags);
        _getCallData = t.GetMethod("GetCallData", flags);
        _resolvedFor = t;
    }

    // Fetch IStubCall.GetCallData() via the cached accessor and coerce the
    // IL2CPP-projected ReadOnlySpan<byte> into a managed byte[]. Returns null
    // on any failure.
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
    // Mirrors WorldNtfStubDispatcher.CoerceCallData exactly.
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
                _log.Warning($"[TeamStubDispatch] no ToArray extractor for {callDataRaw.GetType().FullName}; dispatch disabled");
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
                _log.Warning($"[TeamStubDispatch] ToArray() threw: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }
        if (rawToArr is null) return null;

        return Il2CppSpanCoercion.CoerceToByteArray(rawToArr);
    }

    // Locate GrpcTeamNtfStub.OnCallStub(IStubCall) via reflection. Same
    // strategy as WorldNtfStubDispatcher: AccessTools.TypeByName then walk
    // declared methods matching name + parameter count = 1.
    private MethodInfo? ResolveCallStubMethod()
    {
        Type? stubType;
        try
        {
            stubType = AccessTools.TypeByName(TargetTypeName);
        }
        catch (Exception ex)
        {
            _log.Warning($"[TeamStubDispatch] AccessTools.TypeByName({TargetTypeName}) threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        if (stubType is null)
        {
            _log.Warning($"[TeamStubDispatch] type {TargetTypeName} not found in any loaded assembly; not installed");
            return null;
        }

        return FindOnCallStubOverload(stubType);
    }

    // Walk declared instance methods looking for the OnCallStub(IStubCall)
    // overload. Match by name + parameter count = 1.
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
            _log.Warning($"[TeamStubDispatch] GetMethods({stubType.FullName}) threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        foreach (var m in methods)
        {
            if (m.Name != TargetMethodName) continue;
            try { if (m.GetParameters().Length == 1) return m; }
            catch { /* skip metadata edge cases */ }
        }

        _log.Warning($"[TeamStubDispatch] {TargetTypeName}.{TargetMethodName}(IStubCall) not found; not installed");
        return null;
    }
}
