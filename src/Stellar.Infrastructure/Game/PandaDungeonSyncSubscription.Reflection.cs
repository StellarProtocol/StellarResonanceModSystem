using System;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-facing helpers for <see cref="PandaDungeonSyncSubscription"/>:
/// walk <c>SyncDungeonDirtyDataMessageEvent.VData</c>
/// (<c>Zproto.BufferStream</c>) → <c>.Buffer</c>
/// (<c>Google.Protobuf.ByteString</c>) → <c>ToByteArray()</c> and coerce the
/// IL2CPP array into a managed <c>byte[]</c>. Accessors are resolved ONCE per
/// concrete type and cached (same pattern as
/// <see cref="WorldNtfStubDispatcher"/>) so the per-delta path only pays
/// <c>Invoke</c>. Every failure returns <see langword="null"/> — never throws
/// toward the IL2CPP boundary.
/// </summary>
internal sealed partial class PandaDungeonSyncSubscription
{
    // Accessors cached per concrete runtime type (interop types are stable for
    // the process lifetime; the guards only re-resolve if a different concrete
    // type ever shows up).
    private PropertyInfo? _vDataProp;
    private Type? _eventResolvedFor;
    private PropertyInfo? _bufferProp;
    private FieldInfo? _bufferField;
    private Type? _streamResolvedFor;
    private MethodInfo? _toByteArray;
    private PropertyInfo? _lengthProp;
    private Type? _byteStringResolvedFor;

    // event → VData (BufferStream) → Buffer (ByteString) → managed byte[].
    private byte[]? ExtractBlob(object evt)
    {
        try
        {
            var bufferStream = ReadVData(evt);
            if (bufferStream is null) return null;

            var byteString = ReadBuffer(bufferStream);
            if (byteString is null) return null;

            return CopyByteString(byteString);
        }
        catch
        {
            return null;
        }
    }

    private object? ReadVData(object evt)
    {
        var t = evt.GetType();
        if (!ReferenceEquals(t, _eventResolvedFor))
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _vDataProp = t.GetProperty("VData", flags) ?? t.GetProperty("_VData_k__BackingField", flags);
            _eventResolvedFor = t;
        }
        return _vDataProp?.GetValue(evt);
    }

    // BufferStream.Buffer is a FIELD in the game assembly; the interop
    // projection may surface it as either a property or a field — try both.
    private object? ReadBuffer(object bufferStream)
    {
        var t = bufferStream.GetType();
        if (!ReferenceEquals(t, _streamResolvedFor))
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _bufferProp = t.GetProperty("Buffer", flags);
            _bufferField = _bufferProp is null ? t.GetField("Buffer", flags) : null;
            _streamResolvedFor = t;
        }
        if (_bufferProp is not null) return _bufferProp.GetValue(bufferStream);
        return _bufferField?.GetValue(bufferStream);
    }

    // ByteString.ToByteArray() → Il2CppStructArray<byte> (or byte[]) → managed
    // copy, trimmed to ByteString.Length. The ByteString is POOLED
    // (Rent/Return) and its backing memory can outlive its logical window, so
    // Length is the authoritative byte count — the same count lua's OnSync
    // receives.
    private byte[]? CopyByteString(object byteString)
    {
        var t = byteString.GetType();
        if (!ReferenceEquals(t, _byteStringResolvedFor))
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _toByteArray = t.GetMethod("ToByteArray", flags, binder: null, types: Type.EmptyTypes, modifiers: null);
            _lengthProp = t.GetProperty("Length", flags);
            _byteStringResolvedFor = t;
        }

        if (_toByteArray is null) return null;
        var raw = _toByteArray.Invoke(byteString, null);
        if (raw is null) return null;

        var arr = Il2CppSpanCoercion.CoerceToByteArray(raw);
        if (arr is null) return null;

        return TrimToLength(arr, byteString);
    }

    // Trim the copy to the ByteString's logical Length when it is shorter than
    // the materialised array (pooled backing buffer). An unreadable or
    // out-of-range Length falls back to the full array.
    private byte[] TrimToLength(byte[] arr, object byteString)
    {
        try
        {
            if (_lengthProp?.GetValue(byteString) is int len && len >= 0 && len < arr.Length)
            {
                var trimmed = new byte[len];
                Array.Copy(arr, trimmed, len);
                return trimmed;
            }
        }
        catch { /* fall through — full array */ }
        return arr;
    }
}
