using System;
using System.Reflection;
using HarmonyLib;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-facing helpers for <see cref="WorldNtfLuaStubDispatcher"/> — a
/// clone of <see cref="WorldNtfStubDispatcher"/>'s header-read / payload-extract
/// path, retargeted at the Lua stub. Header/payload accessors are compiled once
/// per concrete stub <see cref="Type"/> by the shared <see cref="StubCallAccessors"/>;
/// any failure translates to false/null.
/// </summary>
internal sealed partial class WorldNtfLuaStubDispatcher
{
    private StubCallAccessors? _accessors;
    private StubCallAccessors Accessors => _accessors ??= new StubCallAccessors(_log, "[LuaStubDispatch]");

    private bool TryReadHeader(object stubCall, out ulong uuid, out uint methodId)
        => Accessors.TryReadHeader(stubCall, out uuid, out methodId);

    private byte[]? ExtractPayload(object stubCall)
    {
        var callDataRaw = Accessors.GetCallData(stubCall);
        return callDataRaw is null ? null : CoerceCallData(callDataRaw);
    }

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
                _log.Warning($"[LuaStubDispatch] no ToArray extractor for {callDataRaw.GetType().FullName}; dispatch disabled");
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
                _log.Warning($"[LuaStubDispatch] ToArray() threw: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }
        if (rawToArr is null) return null;

        return Il2CppSpanCoercion.CoerceToByteArray(rawToArr);
    }

    private MethodInfo? ResolveCallStubMethod()
    {
        Type? stubType;
        try
        {
            stubType = AccessTools.TypeByName(TargetTypeName);
        }
        catch (Exception ex)
        {
            _log.Warning($"[LuaStubDispatch] AccessTools.TypeByName({TargetTypeName}) threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        if (stubType is null)
        {
            _log.Warning($"[LuaStubDispatch] type {TargetTypeName} not found in any loaded assembly; not installed");
            return null;
        }

        return FindOnCallStubOverload(stubType);
    }

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
            _log.Warning($"[LuaStubDispatch] GetMethods({stubType.FullName}) threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        foreach (var m in methods)
        {
            if (m.Name != TargetMethodName) continue;
            try { if (m.GetParameters().Length == 1) return m; }
            catch { /* skip metadata edge cases */ }
        }

        _log.Warning($"[LuaStubDispatch] {TargetTypeName}.{TargetMethodName}(IStubCall) not found; not installed");
        return null;
    }
}
