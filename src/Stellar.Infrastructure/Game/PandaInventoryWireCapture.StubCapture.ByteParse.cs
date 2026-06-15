using System;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Raw-wire byte-parse decode for the WorldNtf SyncContainerData capture
/// (<see cref="PandaInventoryWireCapture"/>'s StubCapture partial). This is the
/// PRIMARY decode path — it mirrors the two proven external reference decoders
/// (StarResonanceAutoMod's <c>packet_capture.py</c> and BPSR-B's
/// <c>world_notify_sync_container_data.py</c>), both of which IGNORE the game's
/// already-decoded message object and byte-parse the immutable wire payload
/// themselves.
///
/// <para>Why this is required: by the time our <c>OnCallStub</c> postfix runs,
/// the game has already DRAINED the nested containers of the live message
/// object that <c>GetCallMsg()</c> returns — its <c>Mod</c> and
/// <c>ItemPackage</c> read back null (only scalars survive). Parsing the raw
/// <c>GetCallData()</c> bytes into a FRESH wrapper produces a fully-populated
/// <c>VData</c>.</para>
///
/// <para>Critical shape detail: the method-21 payload is the WRAPPER
/// <c>Zproto.WorldNtf.Types.SyncContainerData</c> (a single field
/// <c>VData : CharSerialize</c> at field #1), NOT a bare <c>CharSerialize</c>.
/// We resolve that wrapper Type from the live <c>GetCallMsg()</c> object's
/// concrete IL2CPP class (the proven resolution path the GetCallMsg fallback
/// already uses — avoids name-collision risk from a blind short-name lookup),
/// parse the bytes into it, then read its lone CharSerialize member
/// (<c>VData</c>) via the same member-reader the candidate resolver uses.</para>
/// </summary>
internal sealed partial class PandaInventoryWireCapture
{
    // Lazily-resolved wrapper parse handle + the VData member-reader.
    private MethodInfo? _wrapperParseFrom;        // static ParseFrom(arg) or instance Parser.ParseFrom(arg)
    private object? _wrapperParserInstance;        // null for the static path; the Parser otherwise
    private Type? _wrapperParseArgType;            // the parser arg type: byte[] or an IL2CPP byte-array
    private System.Reflection.ConstructorInfo? _wrapperParseArgCtor; // cached (byte[]) ctor for the IL2CPP arg type
    private PandaInventoryPullReader.CharSerializeMemberReader? _wrapperVDataReader;
    private bool _wrapperResolved;
    private bool _wrapperParseFailLogged;

    // PRIMARY decode: parse GetCallData() bytes into the SyncContainerData
    // wrapper, then read its VData (the fresh CharSerialize). Returns null on any
    // failure so DecodeCharSerialize can fall back to the GetCallMsg object path.
    private object? DecodeFromWrapperBytes(object stubCall, Type stubType)
    {
        EnsureWrapperParseResolved(stubCall, stubType);
        if (_wrapperParseFrom is null || _wrapperVDataReader is null || _wrapperParseArgType is null) return null;

        var bytes = ExtractStubPayloadBytes(stubCall, stubType);
        if (bytes is null || bytes.Length == 0) return null;

        object? arg = ToParseArg(bytes, _wrapperParseArgType);
        if (arg is null) return null;

        object? wrapper;
        try { wrapper = _wrapperParseFrom.Invoke(_wrapperParserInstance, new[] { arg }); }
        catch (Exception ex)
        {
            if (!_wrapperParseFailLogged)
            {
                _wrapperParseFailLogged = true;
                _log.Warning($"[Inventory] SyncContainerData wrapper ParseFrom threw: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }
        if (wrapper is null) return null;

        return _wrapperVDataReader.Read(wrapper);
    }

    // Coerce the managed payload into the parser's expected argument type. The
    // managed-byte[] case is identity; the IL2CPP byte-array case constructs an
    // Il2CppStructArray<byte> via its (byte[]) ctor (same bridge PandaChatProbe
    // uses for the send path).
    private object? ToParseArg(byte[] bytes, Type argType)
    {
        if (argType == typeof(byte[])) return bytes;
        try
        {
            return _wrapperParseArgCtor?.Invoke(new object[] { bytes });
        }
        catch (Exception ex)
        {
            if (!_wrapperParseFailLogged)
            {
                _wrapperParseFailLogged = true;
                _log.Warning($"[Inventory] could not build {argType.Name} from byte[]: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }
    }

    // Find a (byte[]) ctor on the IL2CPP array arg type. If the parameter is the
    // abstract Il2CppArrayBase<byte>, build the concrete Il2CppStructArray<byte>
    // in the same assembly instead.
    private static System.Reflection.ConstructorInfo? ResolveIl2CppByteArrayCtor(Type argType)
    {
        var direct = argType.GetConstructor(new[] { typeof(byte[]) });
        if (direct is not null) return direct;

        var open = argType.Assembly.GetType("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1");
        var closed = open?.MakeGenericType(typeof(byte));
        return closed?.GetConstructor(new[] { typeof(byte[]) });
    }

    // One-shot resolution of the wrapper type's byte[] parser + its VData reader.
    // Resolves the wrapper Type from the LIVE message object so we parse into the
    // exact type the game uses (no blind name lookup). Logs the outcome of each
    // step once under diagnostics so an in-world run pinpoints any miss.
    private void EnsureWrapperParseResolved(object stubCall, Type stubType)
    {
        if (_wrapperResolved) return;
        _wrapperResolved = true;

        var wrapperType = ResolveWrapperTypeFromLiveMsg(stubCall, stubType);
        if (wrapperType is null) { DiagWrapperResolve("wrapperType=null"); return; }

        var charSerializeType = ResolveCharSerializeType();
        if (charSerializeType is null) { DiagWrapperResolve("charSerializeType=null"); return; }

        ResolveWrapperParseHandle(wrapperType);
        if (_wrapperParseFrom is null)
        {
            DiagWrapperParseSignatures(wrapperType);
            DiagWrapperResolve($"no ParseFrom on {wrapperType.FullName}");
            return;
        }

        // Cache the IL2CPP byte-array ctor once (the parser arg type is now known)
        // so ToParseArg doesn't re-resolve it on every method-21 sync.
        if (_wrapperParseArgType is not null && _wrapperParseArgType != typeof(byte[]))
        {
            _wrapperParseArgCtor = ResolveIl2CppByteArrayCtor(_wrapperParseArgType);
        }

        _wrapperVDataReader = PandaInventoryPullReader.TryBuildInstanceCharSerializeMemberReader(wrapperType, charSerializeType);
        if (_wrapperVDataReader is null) { DiagWrapperResolve($"no VData reader on {wrapperType.FullName}"); return; }

        DiagWrapperResolve($"OK type={wrapperType.FullName}, VData={_wrapperVDataReader.MemberName}, parser={(_wrapperParserInstance is null ? "static" : "instance")}");
    }

    // Resolve the wrapper managed Type from the live GetCallMsg() object's
    // concrete IL2CPP class — the same proven path DecodeFromCallMsg uses.
    private Type? ResolveWrapperTypeFromLiveMsg(object stubCall, Type stubType)
    {
        var getCallMsg = stubType.GetMethod("GetCallMsg", BindingFlags.Instance | BindingFlags.Public);
        if (getCallMsg is null) return null;
        object? msg;
        try { msg = getCallMsg.Invoke(stubCall, null); }
        catch { return null; }
        if (msg is null) return null;

        var concreteName = ResolveIl2CppConcreteType(msg);
        return ResolveConcreteManagedType(concreteName, msg);
    }

    // Resolve the generated byte-array parser for the wrapper: static
    // ParseFrom(<byte-array>) first, else the static Parser property → instance
    // ParseFrom(<byte-array>). Accepts a managed byte[] OR an IL2CPP byte-array
    // (Il2CppInterop-generated protobuf exposes ParseFrom(Il2CppStructArray<byte>)
    // rather than a managed byte[]). Stores the method, the parser receiver (when
    // instance), and the concrete argument type for ToParseArg.
    private void ResolveWrapperParseHandle(Type wrapperType)
    {
        var staticParse = FindByteArrayLikeParseFrom(wrapperType, BindingFlags.Static | BindingFlags.Public);
        if (staticParse is not null)
        {
            _wrapperParseFrom = staticParse;
            _wrapperParserInstance = null;
            _wrapperParseArgType = staticParse.GetParameters()[0].ParameterType;
            return;
        }

        var parserProp = wrapperType.GetProperty("Parser", BindingFlags.Static | BindingFlags.Public);
        if (parserProp is null) return;
        object? parser;
        try { parser = parserProp.GetValue(null); }
        catch { return; }
        if (parser is null) return;

        var instanceParse = FindByteArrayLikeParseFrom(parser.GetType(), BindingFlags.Instance | BindingFlags.Public);
        if (instanceParse is null) return;
        _wrapperParseFrom = instanceParse;
        _wrapperParserInstance = parser;
        _wrapperParseArgType = instanceParse.GetParameters()[0].ParameterType;
    }

    // A single-parameter ParseFrom whose argument is a byte array (managed byte[]
    // or an IL2CPP byte-array projection). Prefers the managed byte[] overload.
    private static MethodInfo? FindByteArrayLikeParseFrom(Type type, BindingFlags flags)
    {
        MethodInfo[] methods;
        try { methods = type.GetMethods(flags); }
        catch { return null; }

        MethodInfo? il2cppMatch = null;
        foreach (var m in methods)
        {
            if (m.Name != "ParseFrom") continue;
            if (m.IsGenericMethodDefinition) continue;
            ParameterInfo[] ps;
            try { ps = m.GetParameters(); }
            catch { continue; }
            if (ps.Length != 1) continue;
            var pt = ps[0].ParameterType;
            if (pt == typeof(byte[])) return m;              // managed byte[] — preferred.
            if (IsIl2CppByteArray(pt)) il2cppMatch ??= m;     // IL2CPP byte-array fallback.
        }
        return il2cppMatch;
    }

    // True for an IL2CPP byte-array parameter (Il2CppStructArray<byte> or the
    // abstract Il2CppArrayBase<byte> it derives from).
    private static bool IsIl2CppByteArray(Type t)
    {
        if (!t.IsGenericType) return false;
        if (t.GetGenericArguments() is not { Length: 1 } args || args[0] != typeof(byte)) return false;
        var n = t.Name;
        return n.StartsWith("Il2CppStructArray", StringComparison.Ordinal)
            || n.StartsWith("Il2CppArrayBase", StringComparison.Ordinal);
    }

}
