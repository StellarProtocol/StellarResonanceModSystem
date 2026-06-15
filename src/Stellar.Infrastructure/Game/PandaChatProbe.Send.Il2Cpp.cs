using System;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// IL2CPP coercion bridge for <see cref="PandaChatProbe"/>'s send path.
/// Boxes a managed <c>byte[]</c> into an <c>Il2CppSystem.ReadOnlySpan&lt;byte&gt;</c>
/// instance so reflection-driven <c>ZTcpClient.Send</c> invocation passes the
/// runtime parameter-type check.
///
/// The IL2CPP-projected <c>ReadOnlySpan&lt;byte&gt;</c> exposes different
/// single-arg constructors depending on the interop assembly version. This
/// partial tries each known shape in preference order — see the ctor-fallback
/// helpers below.
/// </summary>
internal sealed partial class PandaChatProbe
{
    /// <summary>
    /// Box a managed <c>byte[]</c> into an <c>Il2CppSystem.ReadOnlySpan&lt;byte&gt;</c>
    /// so the reflection Invoke against <c>ZTcpClient.Send(ReadOnlySpan&lt;byte&gt;)</c>
    /// passes type-check. On the first call, resolves and caches the chosen
    /// single-arg constructor; subsequent calls reuse the cached
    /// <see cref="_il2cppSpanCtor"/>.
    /// </summary>
    private object WrapBytesAsIl2CppSpan(byte[] managed)
    {
        if (_il2cppSpanCtor is null || _il2cppSpanType is null)
        {
            ResolveIl2CppSpanCtor();
        }

        var ctorParamType = _il2cppSpanCtor!.GetParameters()[0].ParameterType;
        object ctorArg;
        if (ctorParamType == typeof(byte[]))
        {
            ctorArg = managed;
        }
        else if (ctorParamType.Name == "Il2CppStructArray`1" || ctorParamType.FullName?.IndexOf("Il2CppStructArray", StringComparison.Ordinal) >= 0)
        {
            ctorArg = TryConstructIl2CppSpanViaCtor(ctorParamType, managed);
        }
        else if (ctorParamType.Name == "Il2CppArrayBase`1" || ctorParamType.FullName?.IndexOf("Il2CppArrayBase", StringComparison.Ordinal) >= 0)
        {
            ctorArg = TryConstructIl2CppSpanViaArrayBox(ctorParamType, managed);
        }
        else
        {
            throw new InvalidOperationException(
                $"unsupported ReadOnlySpan<byte> ctor parameter type: {ctorParamType.FullName}");
        }

        return _il2cppSpanCtor.Invoke(new[] { ctorArg });
    }

    /// <summary>
    /// One-shot resolution of the IL2CPP <c>ReadOnlySpan&lt;byte&gt;</c>
    /// single-arg constructor. Caches the chosen ctor + parameter type into
    /// <see cref="_il2cppSpanCtor"/> and <see cref="_il2cppSpanType"/>.
    /// Throws if no usable single-arg ctor is found — chat send is non-functional
    /// in that case and we want to surface the failure loudly at first attempt.
    /// </summary>
    private void ResolveIl2CppSpanCtor()
    {
        var spanType = _tcpClientSend!.GetParameters()[0].ParameterType;
        _il2cppSpanType = spanType;

        // Look for any single-arg ctor we can drive from a managed byte[]. The
        // IL2CPP-projected ReadOnlySpan<byte> typically exposes:
        //   ctor(Il2CppStructArray<byte>)
        //   ctor(byte[])                 — sometimes available
        //   ctor(IntPtr, int)            — last-resort, pinned pointer
        ConstructorInfo? chosenCtor = null;
        foreach (var c in spanType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var ps = c.GetParameters();
            if (ps.Length != 1) continue;
            var pt = ps[0].ParameterType;
            if (pt == typeof(byte[]))
            {
                chosenCtor = c;
                break;
            }
            // First match wins among single-arg constructors; we'll handle the
            // managed-byte[] -> Il2CppStructArray<byte> coercion at invoke time.
            if (chosenCtor is null) chosenCtor = c;
        }

        if (chosenCtor is null)
        {
            throw new InvalidOperationException(
                $"no single-arg constructor on {spanType.FullName} suitable for byte[] coercion");
        }
        _il2cppSpanCtor = chosenCtor;
        _log.Info($"[ChatProbe] resolved ReadOnlySpan<byte> ctor: ({chosenCtor.GetParameters()[0].ParameterType.FullName})");
    }

    /// <summary>
    /// Construct the ctor argument when the ReadOnlySpan&lt;byte&gt; ctor takes
    /// <c>Il2CppStructArray&lt;byte&gt;</c> directly — invoke its
    /// <c>(byte[])</c> ctor and return the boxed instance.
    /// </summary>
    private static object TryConstructIl2CppSpanViaCtor(Type ctorParamType, byte[] managed)
    {
        // The ctor signature is Il2CppStructArray<T>(T[]).
        var structArrCtor = ctorParamType.GetConstructor(new[] { typeof(byte[]) });
        if (structArrCtor is null)
        {
            throw new InvalidOperationException(
                $"Il2CppStructArray<byte>(byte[]) ctor not found on {ctorParamType.FullName}");
        }
        return structArrCtor.Invoke(new object[] { managed });
    }

    /// <summary>
    /// Construct the ctor argument when the ReadOnlySpan&lt;byte&gt; ctor takes
    /// the abstract <c>Il2CppArrayBase&lt;byte&gt;</c> base type — locate the
    /// concrete <c>Il2CppStructArray&lt;byte&gt;</c> in the same assembly,
    /// invoke its <c>(byte[])</c> ctor, and return the boxed instance.
    /// </summary>
    private static object TryConstructIl2CppSpanViaArrayBox(Type ctorParamType, byte[] managed)
    {
        // The ctor declares Il2CppArrayBase<byte> (the abstract base). Build
        // an Il2CppStructArray<byte> instead — it inherits from
        // Il2CppArrayBase<byte> and is the concrete implementation for value
        // types. Locate the type by name in the Il2CppInterop runtime
        // assembly and invoke its byte[] ctor.
        var arrayBaseAsm = ctorParamType.Assembly;
        var structArrayOpenType = arrayBaseAsm.GetType("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1");
        if (structArrayOpenType is null)
        {
            throw new InvalidOperationException(
                $"Il2CppStructArray`1 not found in {arrayBaseAsm.FullName} (needed to bridge Il2CppArrayBase<byte>)");
        }
        var structArrayClosedType = structArrayOpenType.MakeGenericType(typeof(byte));
        var structArrCtor = structArrayClosedType.GetConstructor(new[] { typeof(byte[]) });
        if (structArrCtor is null)
        {
            throw new InvalidOperationException(
                $"Il2CppStructArray<byte>(byte[]) ctor not found on {structArrayClosedType.FullName}");
        }
        return structArrCtor.Invoke(new object[] { managed });
    }
}
