using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Payload decode + diagnostics for the WorldNtf SyncContainerData capture
/// (<see cref="PandaInventoryWireCapture"/>'s StubCapture partial). Turns a method-21
/// stub call into a <c>Zproto.CharSerialize</c> and surfaces the decoded body
/// shape under <c>STELLAR_DIAGNOSTICS=1</c>.
///
/// <para>The PRIMARY path (byte-parse the wrapper → read <c>VData</c>) lives in
/// the sibling <c>PandaInventoryWireCapture.StubCapture.ByteParse.cs</c>. This file
/// holds the FALLBACK paths, tried only when the byte-parse fails:</para>
/// <list type="number">
///   <item><b>GetCallMsg()</b> — the stub's already-decoded message. If it IS a
///   CharSerialize, use it directly; if it's a wrapper, read a
///   CharSerialize-typed member off it. On this build <c>OnCallStub</c> has
///   drained the message's nested containers by the time the postfix runs, so
///   this typically yields a CharSerialize with <c>Mod</c>/<c>ItemPackage</c>
///   null — hence it is no longer preferred.</item>
///   <item><b>direct byte-parse</b> — coerce <c>GetCallData()</c> into a managed
///   <c>byte[]</c> (via <see cref="Il2CppSpanCoercion"/>) and parse with the
///   generated <c>CharSerialize.Parser.ParseFrom(byte[])</c>. Wrong shape on this
///   build (the payload is the wrapper, not a bare CharSerialize); kept only as a
///   last resort for builds where the wrapper type can't be resolved.</item>
/// </list>
/// </summary>
internal sealed partial class PandaInventoryWireCapture
{
    // Returns the decoded CharSerialize for a method-21 stub call, or null.
    //
    // Priority (matches the two proven external reference decoders):
    //   1. byte-parse the raw payload into the SyncContainerData WRAPPER and read
    //      its VData — a FRESH parse of the immutable wire bytes, so Mod +
    //      ItemPackage are fully populated (DecodeFromWrapperBytes).
    //   2. GetCallMsg() object path — the game's already-decoded message. On this
    //      build OnCallStub has drained its nested containers by the time the
    //      postfix runs (Mod/ItemPackage read back null), so this is a fallback
    //      only, used when byte extraction/parse fails (DecodeFromCallMsg).
    //   3. legacy: byte-parse the payload directly as CharSerialize. Wrong shape
    //      on this build (the payload is the wrapper), kept only as a last resort
    //      for builds where the wrapper type can't be resolved.
    private object? DecodeCharSerialize(object stubCall, Type stubType)
    {
        var fromWrapper = DecodeFromWrapperBytes(stubCall, stubType);
        if (fromWrapper is not null) return fromWrapper;

        var fromMsg = DecodeFromCallMsg(stubCall, stubType);
        if (fromMsg is not null) return fromMsg;

        return DecodeFromCallDataBytes(stubCall, stubType);
    }

    // Decode path 1: the stub's already-parsed message. In-world recon confirmed
    // method-21's body is the WRAPPER Zproto.WorldNtf.Types.SyncContainerData,
    // whose `VData` property is the Zproto.CharSerialize. GetCallMsg returns the
    // message projected to its Google.Protobuf.IBufferMessage interface, so
    // msg.GetType() does NOT expose the wrapper's VData member — we must resolve
    // the concrete managed type (by the IL2CPP class name) and Cast<T>() the
    // boxed message to it before reading the member.
    private object? DecodeFromCallMsg(object stubCall, Type stubType)
    {
        var getCallMsg = stubType.GetMethod("GetCallMsg", BindingFlags.Instance | BindingFlags.Public);
        if (getCallMsg is null) return null;

        object? msg;
        try { msg = getCallMsg.Invoke(stubCall, null); }
        catch { return null; }
        if (msg is null) return null;

        var charSerializeType = ResolveCharSerializeType();
        if (charSerializeType is null) return null;

        var concreteName = ResolveIl2CppConcreteType(msg);

        // If method 21 ever delivers a bare CharSerialize, latch it directly.
        if (concreteName is not null && concreteName.EndsWith(".CharSerialize", StringComparison.Ordinal))
        {
            return CastToConcrete(msg, charSerializeType) ?? msg;
        }

        // Wrapper case (the live shape): cast the interface-projected message to
        // its concrete managed type, then read the CharSerialize-typed member.
        var concreteType = ResolveConcreteManagedType(concreteName, msg);
        if (concreteType is null) return null;

        var castMsg = CastToConcrete(msg, concreteType) ?? msg;
        var memberReader = PandaInventoryPullReader.TryBuildInstanceCharSerializeMemberReader(concreteType, charSerializeType);
        if (memberReader is null)
        {
            DiagWrapperMembers(concreteType);
            return null;
        }
        return memberReader.Read(castMsg);
    }

    // Resolve the concrete managed wrapper Type from the IL2CPP class name
    // recovered via il2cpp_object_get_class. Falls back to msg.GetType() (which
    // may be the interface) only if name resolution fails.
    private Type? ResolveConcreteManagedType(string? il2cppName, object msg)
    {
        if (!string.IsNullOrEmpty(il2cppName))
        {
            var byName = _typeRegistry.FindType(il2cppName!);
            if (byName is not null) return byName;
            // Il2CppInterop flattens nested types to `Outer/Inner`; the IL2CPP
            // name uses `Outer.Types.Inner`. Try a short-name match on the leaf.
            var leaf = il2cppName!;
            var lastDot = leaf.LastIndexOf('.');
            if (lastDot >= 0 && lastDot + 1 < leaf.Length) leaf = leaf.Substring(lastDot + 1);
            var byShort = PandaInventoryPullReader.FindTypeByShortName(leaf);
            if (byShort is not null) return byShort;
        }
        var managed = msg.GetType();
        // The interface projection has no readable instance members for our walk.
        return managed.IsInterface ? null : managed;
    }

    // Cast a boxed Il2CppObjectBase to the concrete wrapper type via
    // Il2CppObjectBase.Cast<T>(), mirroring PandaChatProbe's unwrap. Returns null
    // (caller uses the original boxed msg) on any failure.
    private object? CastToConcrete(object msg, Type concreteType)
    {
        if (msg is not Il2CppObjectBase) return null;
        try
        {
            EnsureCastMethodResolved();
            if (_il2cppCastOpen is null) return null;
            var closed = _il2cppCastOpen.MakeGenericMethod(concreteType);
            return closed.Invoke(msg, Array.Empty<object>());
        }
        catch { return null; }
    }

    private MethodInfo? _il2cppCastOpen;
    private bool _castResolved;

    private void EnsureCastMethodResolved()
    {
        if (_castResolved) return;
        _castResolved = true;
        _il2cppCastOpen = typeof(Il2CppObjectBase).GetMethod(
            "Cast", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    // Decode path 2: re-parse the raw payload bytes via the generated parser.
    private object? DecodeFromCallDataBytes(object stubCall, Type stubType)
    {
        var bytes = ExtractStubPayloadBytes(stubCall, stubType);
        if (bytes is null || bytes.Length == 0) return null;

        EnsureParseFromResolved();
        if (_charSerializeParseFrom is null) return null;

        try { return _charSerializeParseFrom.Invoke(_charSerializeParserInstance, new object[] { bytes }); }
        catch (Exception ex)
        {
            if (!_stubPayloadFailLogged)
            {
                _stubPayloadFailLogged = true;
                _log.Warning($"[Inventory] CharSerialize.ParseFrom threw: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }
    }

    // Coerce IStubCall.GetCallData() (an IL2CPP-projected ReadOnlySpan<byte>)
    // into a managed byte[]. Mirrors PandaCombatStubProbe.ExtractStubPayloadBytes
    // — the projected get_Item indexer returns the 0x40 sentinel under HarmonyX
    // ref-struct boxing, so we use the shared ToArray() extractor.
    private byte[]? ExtractStubPayloadBytes(object stubCall, Type stubType)
    {
        var getCallData = stubType.GetMethod("GetCallData", BindingFlags.Instance | BindingFlags.Public);
        if (getCallData is null) return null;
        object? callDataRaw;
        try { callDataRaw = getCallData.Invoke(stubCall, null); }
        catch { return null; }
        if (callDataRaw is null) return null;

        if (Il2CppSpanCoercion.SpanToArrayMethod is null
            && System.Threading.Interlocked.CompareExchange(ref Il2CppSpanCoercion.SpanExtractorResolved, 1, 0) == 0)
        {
            Il2CppSpanCoercion.ResolveSpanExtractor(_log, callDataRaw.GetType());
        }

        var toArr = Il2CppSpanCoercion.SpanToArrayMethod;
        if (toArr is null)
        {
            if (!_stubPayloadFailLogged)
            {
                _stubPayloadFailLogged = true;
                _log.Warning($"[Inventory] no ToArray extractor for {callDataRaw.GetType().FullName}; byte-parse fallback disabled");
            }
            return null;
        }

        object? rawToArr;
        try { rawToArr = toArr.Invoke(callDataRaw, null); }
        catch { return null; }
        if (rawToArr is null) return null;
        return Il2CppSpanCoercion.CoerceToByteArray(rawToArr);
    }

    private Type? ResolveCharSerializeType()
        => _typeRegistry.FindType("Zproto.CharSerialize") ?? PandaInventoryPullReader.FindTypeByShortName("CharSerialize");

    // One-shot resolution of the generated CharSerialize parser. Google.Protobuf
    // messages expose a static `Parser` property (a MessageParser<T> with
    // ParseFrom(byte[])) and/or a static ParseFrom(byte[]). Resolve whichever is
    // present. The MessageParser path stores the parser instance; the static
    // ParseFrom path leaves the instance null.
    private void EnsureParseFromResolved()
    {
        if (_parseFromResolved) return;
        _parseFromResolved = true;

        var charSerializeType = ResolveCharSerializeType();
        if (charSerializeType is null) return;

        // Preferred: static ParseFrom(byte[]) directly on the message type.
        var staticParse = FindByteArrayParseFrom(charSerializeType, BindingFlags.Static | BindingFlags.Public);
        if (staticParse is not null)
        {
            _charSerializeParseFrom = staticParse;
            _charSerializeParserInstance = null;
            return;
        }

        // Fallback: static Parser property → instance ParseFrom(byte[]).
        var parserProp = charSerializeType.GetProperty("Parser", BindingFlags.Static | BindingFlags.Public);
        if (parserProp is null) return;
        object? parser;
        try { parser = parserProp.GetValue(null); }
        catch { return; }
        if (parser is null) return;

        var instanceParse = FindByteArrayParseFrom(parser.GetType(), BindingFlags.Instance | BindingFlags.Public);
        if (instanceParse is null) return;
        _charSerializeParseFrom = instanceParse;
        _charSerializeParserInstance = parser;
    }

    private static MethodInfo? FindByteArrayParseFrom(Type type, BindingFlags flags)
    {
        MethodInfo[] methods;
        try { methods = type.GetMethods(flags); }
        catch { return null; }
        foreach (var m in methods)
        {
            if (m.Name != "ParseFrom") continue;
            if (m.IsGenericMethodDefinition) continue;
            ParameterInfo[] ps;
            try { ps = m.GetParameters(); }
            catch { continue; }
            if (ps.Length != 1) continue;
            if (ps[0].ParameterType != typeof(byte[])) continue;
            return m;
        }
        return null;
    }

    // Recover the concrete IL2CPP class FullName for a boxed managed reference
    // whose declared type is just an interface (e.g.
    // Google.Protobuf.IBufferMessage). Mirrors
    // PandaCombatStubProbe.ResolveIl2CppConcreteType. Returns null if the object
    // isn't IL2CPP-backed or the lookup fails.
    private static string? ResolveIl2CppConcreteType(object boxedMsg)
    {
        try
        {
            if (boxedMsg is not Il2CppObjectBase obj) return null;
            var instancePtr = obj.Pointer;
            if (instancePtr == IntPtr.Zero) return null;

            var classPtr = IL2CPP.il2cpp_object_get_class(instancePtr);
            if (classPtr == IntPtr.Zero) return null;

            var nsPtr = IL2CPP.il2cpp_class_get_namespace(classPtr);
            var namePtr = IL2CPP.il2cpp_class_get_name(classPtr);
            var name = Marshal.PtrToStringAnsi(namePtr);
            if (string.IsNullOrEmpty(name)) return null;
            var ns = Marshal.PtrToStringAnsi(nsPtr);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }
        catch
        {
            return null;
        }
    }

    private string DescribeStubMsgShape(object stubCall, Type stubType)
    {
        var getCallMsg = stubType.GetMethod("GetCallMsg", BindingFlags.Instance | BindingFlags.Public);
        if (getCallMsg is null) return "<no GetCallMsg>";
        object? msg;
        try { msg = getCallMsg.Invoke(stubCall, null); }
        catch (Exception ex) { return $"<GetCallMsg threw {ex.GetType().Name}>"; }
        if (msg is null) return "<null msg>";
        return ResolveIl2CppConcreteType(msg) ?? msg.GetType().FullName ?? "<unknown>";
    }
}
