using System;
using System.Linq.Expressions;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Per-packet header/payload reader for an IL2CPP <c>ZCode.ZRpc.IStubCall</c>, shared by the stub dispatchers
/// (<see cref="WorldNtfStubDispatcher"/>, <see cref="WorldNtfLuaStubDispatcher"/>, <see cref="GrpcTeamNtfStubDispatcher"/>).
/// <para>
/// <c>GetUuid</c> / <c>GetMethodId</c> / <c>GetCallData</c> are resolved once per concrete stub <see cref="Type"/> and
/// compiled into typed open-instance delegates, so the per-packet path is a plain call: no <c>MethodInfo.Invoke</c>
/// (an <c>object[]</c> + boxed <c>ulong</c>/<c>uint</c> per call, and reflection dispatch) and no <c>Convert</c>. These
/// postfixes run on the GAME'S MAIN THREAD — every packet, inside <c>ZRpcImpl.ProcessFrameDownPackets</c> (WPR) — so
/// that cost was frame time. If compiling fails the cached <see cref="MethodInfo"/>s are the reflective fallback (same
/// pattern as <see cref="Il2CppSpanCoercion"/>'s ToArray extractor).
/// </para>
/// Not thread-safe by design: each dispatcher's postfix runs on the single main thread.
/// </summary>
internal sealed class StubCallAccessors
{
    private readonly IPluginLog _log;
    private readonly string _tag;
    private Type? _resolvedFor;

    // Fast path (compiled once per concrete type).
    private Func<object, ulong>? _uuid;
    private Func<object, uint>? _methodId;
    private Func<object, object?>? _callData;

    // Reflective fallback, used only when a delegate failed to compile.
    private MethodInfo? _getUuid;
    private MethodInfo? _getMethodId;
    private MethodInfo? _getCallData;

    public StubCallAccessors(IPluginLog log, string tag)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _tag = tag;
    }

    /// <summary>Read (uuid, methodId). False on any failure or missing accessor — caller skips the packet.</summary>
    public bool TryReadHeader(object stubCall, out ulong uuid, out uint methodId)
    {
        uuid = 0;
        methodId = 0;
        try
        {
            var t = stubCall.GetType();
            if (!ReferenceEquals(t, _resolvedFor)) Resolve(t);

            if (_uuid is not null && _methodId is not null)
            {
                uuid = _uuid(stubCall);
                methodId = _methodId(stubCall);
                return true;
            }
            return TryReadHeaderReflective(stubCall, out uuid, out methodId);
        }
        catch
        {
            // Failure on one concrete type — swallow; the next packet may carry a type that succeeds.
            return false;
        }
    }

    /// <summary>
    /// <c>GetCallData()</c> of the stub whose header was just read (same concrete type). Null when unavailable or it
    /// threw. Callers coerce the IL2CPP-projected <c>ReadOnlySpan&lt;byte&gt;</c> via <see cref="Il2CppSpanCoercion"/>.
    /// </summary>
    public object? GetCallData(object stubCall)
    {
        try
        {
            var fast = _callData;
            if (fast is not null) return fast(stubCall);
            return _getCallData?.Invoke(stubCall, null);
        }
        catch { return null; }
    }

    private bool TryReadHeaderReflective(object stubCall, out ulong uuid, out uint methodId)
    {
        uuid = 0;
        methodId = 0;
        if (_getUuid is null || _getMethodId is null) return false;
        var u = _getUuid.Invoke(stubCall, null);
        var m = _getMethodId.Invoke(stubCall, null);
        if (u is null || m is null) return false;
        uuid = Convert.ToUInt64(u);
        methodId = Convert.ToUInt32(m);
        return true;
    }

    // One-shot resolution per concrete stub type: look the accessors up and compile them. Re-runs only when a packet
    // carries a different concrete type than last seen.
    private void Resolve(Type t)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        _getUuid = t.GetMethod("GetUuid", flags);
        _getMethodId = t.GetMethod("GetMethodId", flags);
        _getCallData = t.GetMethod("GetCallData", flags);
        _uuid = Bind<ulong>(t, _getUuid);
        _methodId = Bind<uint>(t, _getMethodId);
        _callData = Bind<object?>(t, _getCallData);
        _resolvedFor = t;
    }

    // Compile (object o) => (TR)((T)o).Method() once. Null (→ reflective fallback) when the accessor is missing or the
    // expression can't be built/compiled; logged once per type so a silent slow path is visible.
    private Func<object, TR>? Bind<TR>(Type t, MethodInfo? mi)
    {
        if (mi is null || mi.GetParameters().Length != 0) return null;
        try
        {
            var p = Expression.Parameter(typeof(object), "o");
            Expression call = Expression.Call(Expression.Convert(p, t), mi);
            if (call.Type != typeof(TR)) call = Expression.Convert(call, typeof(TR));
            return Expression.Lambda<Func<object, TR>>(call, p).Compile();
        }
        catch (Exception ex)
        {
            _log.Warning($"{_tag} {t.Name}.{mi.Name} delegate compile failed (reflective fallback): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
