using System;
using System.Reflection;
using HarmonyLib;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-facing helpers for <see cref="GrpcTeamNtfStubDispatcher"/>. The
/// key perf property lives in the shared <see cref="StubCallAccessors"/>: the
/// <c>GetUuid</c> / <c>GetMethodId</c> / <c>GetCallData</c> accessors are
/// resolved ONCE per concrete stub <see cref="Type"/> and compiled into typed
/// delegates, so the per-packet path is a plain call — no <c>MethodInfo.Invoke</c>.
///
/// <para>
/// Mirrors <see cref="WorldNtfStubDispatcher"/>.<c>Reflection.cs</c> exactly:
/// shared <see cref="Il2CppSpanCoercion"/> for the <c>ToArray()</c> span
/// extraction; same per-type accessor cache. Reflection or marshalling
/// failures translate to a <c>false</c> / <c>null</c> return so the postfix
/// can swallow the packet silently.
/// </para>
/// </summary>
internal sealed partial class GrpcTeamNtfStubDispatcher
{
    private StubCallAccessors? _accessors;
    private StubCallAccessors Accessors => _accessors ??= new StubCallAccessors(_log, "[TeamStubDispatch]");

    // Read (uuid, methodId) off IStubCall via the per-type compiled accessors.
    private bool TryReadHeader(object stubCall, out ulong uuid, out uint methodId)
        => Accessors.TryReadHeader(stubCall, out uuid, out methodId);

    // Fetch IStubCall.GetCallData() via the cached accessor and coerce the
    // IL2CPP-projected ReadOnlySpan<byte> into a managed byte[]. Returns null
    // on any failure.
    private byte[]? ExtractPayload(object stubCall)
    {
        var callDataRaw = Accessors.GetCallData(stubCall);
        return callDataRaw is null ? null : CoerceCallData(callDataRaw);
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
        try { rawToArr = Il2CppSpanCoercion.InvokeToArray(callDataRaw); }
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
