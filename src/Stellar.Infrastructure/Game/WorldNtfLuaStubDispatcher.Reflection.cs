using System;
using System.Reflection;
using HarmonyLib;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-facing helpers for <see cref="WorldNtfLuaStubDispatcher"/> — a
/// clone of <see cref="WorldNtfStubDispatcher"/>'s header-read / payload-extract
/// path, retargeted at the Lua stub. Accessors are cached per concrete stub
/// <see cref="Type"/>; any failure on the I/O thread translates to false/null.
/// </summary>
internal sealed partial class WorldNtfLuaStubDispatcher
{
    private MethodInfo? _getUuid;
    private MethodInfo? _getMethodId;
    private MethodInfo? _getCallData;
    private Type? _resolvedFor;

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

    private void ResolveAccessors(Type t)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        _getUuid = t.GetMethod("GetUuid", flags);
        _getMethodId = t.GetMethod("GetMethodId", flags);
        _getCallData = t.GetMethod("GetCallData", flags);
        _resolvedFor = t;
    }

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
        try { rawToArr = toArr.Invoke(callDataRaw, null); }
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
