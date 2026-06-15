using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Generic IL2CPP / proto MapField + RepeatedField walking helpers for
/// <see cref="PandaInventoryPullReader"/>. Mirrors the typed-enumerator pattern
/// from <c>PandaGameDataProbe.Iteration.cs</c>: the BCL non-generic
/// <c>IEnumerable</c> shim returned by Il2CppInterop proxies yields zero
/// rows, so we resolve the typed parameterless <c>GetEnumerator()</c>
/// instead and walk via <c>MoveNext()</c> + <c>Current</c>.
/// </summary>
internal sealed partial class PandaInventoryPullReader
{
    /// <summary>
    /// Look up a value in a MapField/IDictionary-like collection by integer
    /// key without boxing the entire dictionary. Falls back to iteration if
    /// no fast indexer is available.
    /// </summary>
    internal static object? LookupMapValue(object map, int key)
    {
        var mapType = map.GetType();
        // Fast path: indexer (MapField<int, V>.this[int] / Dictionary<int, V>.this[int]).
        var indexer = mapType.GetMethod("get_Item", AnyInstance, binder: null,
            types: new[] { typeof(int) }, modifiers: null);
        if (indexer is not null)
        {
            try { return indexer.Invoke(map, new object[] { key }); }
            catch { /* fall through */ }
        }

        foreach (var (k, v) in EnumerateMapEntries(map))
        {
            if (AsInt32(k) == key) return v;
        }
        return null;
    }

    /// <summary>
    /// Enumerate (key, value) pairs from any map-like IL2CPP / proto object.
    /// Tries the typed parameterless GetEnumerator first (the pattern used
    /// by Il2CppInterop proxies); falls back to BCL IEnumerable if available.
    /// </summary>
    internal static IEnumerable<(object? key, object? value)> EnumerateMapEntries(object map)
    {
        var mapType = map.GetType();
        var typedGetEnumerator = FindParameterlessGetEnumerator(mapType);

        object? enumerator;
        try { enumerator = typedGetEnumerator?.Invoke(map, Array.Empty<object>()); }
        catch { enumerator = null; }

        if (enumerator is not null)
        {
            foreach (var pair in EnumerateTypedKvp(enumerator))
            {
                yield return pair;
            }
            yield break;
        }

        foreach (var pair in EnumerateBclKvp(map))
        {
            yield return pair;
        }
    }

    // Finds the parameterless GetEnumerator method on a map type, or null.
    private static MethodInfo? FindParameterlessGetEnumerator(Type mapType)
    {
        foreach (var m in mapType.GetMethods(AnyInstance))
        {
            if (m.Name != "GetEnumerator") continue;
            if (m.GetParameters().Length != 0) continue;
            return m;
        }
        return null;
    }

    // Walks a typed IL2CPP enumerator object, yielding (key, value) pairs from
    // its KeyValuePair-shaped Current. Disposes the enumerator when done.
    private static IEnumerable<(object? key, object? value)> EnumerateTypedKvp(object enumerator)
    {
        var et = enumerator.GetType();
        var moveNext = et.GetMethod("MoveNext", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);
        var current = et.GetProperty("Current", AnyInstance);
        if (moveNext is null || current is null)
        {
            TryDispose(enumerator);
            yield break;
        }

        PropertyInfo? keyProp = null;
        PropertyInfo? valueProp = null;
        const int safetyCap = 100_000;
        for (var i = 0; i < safetyCap; i++)
        {
            bool advanced;
            try { advanced = (bool)(moveNext.Invoke(enumerator, Array.Empty<object>()) ?? false); }
            catch { break; }
            if (!advanced) break;
            object? kvp;
            try { kvp = current.GetValue(enumerator); }
            catch { continue; }
            if (kvp is null) continue;
            if (keyProp is null)
            {
                keyProp = kvp.GetType().GetProperty("Key", AnyInstance);
                valueProp = kvp.GetType().GetProperty("Value", AnyInstance);
                if (keyProp is null || valueProp is null) break;
            }
            object? k, v;
            try { k = keyProp.GetValue(kvp); v = valueProp!.GetValue(kvp); }
            catch { continue; }
            yield return (k, v);
        }
        TryDispose(enumerator);
    }

    // Walks a BCL IEnumerable of KeyValuePair-shaped items, yielding (key, value) pairs.
    private static IEnumerable<(object? key, object? value)> EnumerateBclKvp(object map)
    {
        if (map is not IEnumerable bclEnum) yield break;
        foreach (var item in bclEnum)
        {
            if (item is null) continue;
            var it = item.GetType();
            var kp = it.GetProperty("Key", AnyInstance);
            var vp = it.GetProperty("Value", AnyInstance);
            if (kp is null || vp is null) continue;
            object? k, v;
            try { k = kp.GetValue(item); v = vp.GetValue(item); }
            catch { continue; }
            yield return (k, v);
        }
    }

    /// <summary>
    /// Enumerate VALUES from a map-like object (skips the key).
    /// </summary>
    internal static IEnumerable<object?> EnumerateMapValues(object map)
    {
        foreach (var (_, v) in EnumerateMapEntries(map))
        {
            yield return v;
        }
    }

    /// <summary>
    /// Materialize a map-like object into a fresh Dictionary keyed by TKey,
    /// suitable for repeat lookup. Skips entries whose key doesn't coerce.
    /// </summary>
    private static Dictionary<TKey, object> MaterializeKeyedDict<TKey>(object map)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, object>(capacity: 16);
        foreach (var (k, v) in EnumerateMapEntries(map))
        {
            if (k is null || v is null) continue;
            if (k is TKey typedKey)
            {
                result[typedKey] = v;
                continue;
            }
            // Numeric coercion: MapField<long, V> may yield boxed long for an int key request and vice versa.
            if (typeof(TKey) == typeof(long) && k is int kInt)
            {
                result[(TKey)(object)(long)kInt] = v;
            }
            else if (typeof(TKey) == typeof(int) && k is long kLong)
            {
                result[(TKey)(object)unchecked((int)kLong)] = v;
            }
        }
        return result;
    }

    /// <summary>
    /// Walk a RepeatedField/IList of ints into a BCL List&lt;int&gt;.
    /// Tolerant of either BCL IEnumerable (proto's RepeatedField does implement it)
    /// or an Il2CppInterop proxy that needs the typed enumerator.
    /// </summary>
    internal static IList<int> CollectInts(object? raw)
    {
        if (raw is null) return Array.Empty<int>();
        if (raw is int[] arr) return arr;
        var result = new List<int>(capacity: 4);
        if (raw is IEnumerable bcl)
        {
            foreach (var item in bcl)
            {
                result.Add(AsInt32(item));
            }
            return result;
        }
        // Fallback: typed enumerator (mirrors EnumerateMapEntries shape).
        var t = raw.GetType();
        var getEnum = t.GetMethod("GetEnumerator", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);
        if (getEnum is null) return result;
        object? e;
        try { e = getEnum.Invoke(raw, Array.Empty<object>()); }
        catch { return result; }
        if (e is null) return result;
        var et = e.GetType();
        var moveNext = et.GetMethod("MoveNext", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);
        var current = et.GetProperty("Current", AnyInstance);
        if (moveNext is null || current is null) { TryDispose(e); return result; }
        for (var i = 0; i < 100_000; i++)
        {
            bool advanced;
            try { advanced = (bool)(moveNext.Invoke(e, Array.Empty<object>()) ?? false); }
            catch { break; }
            if (!advanced) break;
            try { result.Add(AsInt32(current.GetValue(e))); } catch { /* ignore */ }
        }
        TryDispose(e);
        return result;
    }

    internal static int AsInt32(object? v) => v switch
    {
        int i => i,
        long l => unchecked((int)l),
        uint u => unchecked((int)u),
        ulong ul => unchecked((int)ul),
        short s => s,
        ushort us => us,
        byte b => b,
        _ => 0,
    };

    internal static long AsInt64(object? v) => v switch
    {
        long l => l,
        int i => i,
        uint u => u,
        ulong ul => unchecked((long)ul),
        short s => s,
        ushort us => us,
        byte b => b,
        _ => 0L,
    };

    private static int TryReadInt32(object source, PropertyInfo? prop)
    {
        if (prop is null) return 0;
        try { return AsInt32(prop.GetValue(source)); }
        catch { return 0; }
    }

    private static long TryReadInt64(object source, PropertyInfo? prop)
    {
        if (prop is null) return 0L;
        try { return AsInt64(prop.GetValue(source)); }
        catch { return 0L; }
    }

    private static void TryDispose(object? maybeDisposable)
    {
        if (maybeDisposable is IDisposable d)
        {
            try { d.Dispose(); } catch { /* ignore */ }
            return;
        }
        var disposeMethod = maybeDisposable?.GetType().GetMethod("Dispose", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);
        if (disposeMethod is not null)
        {
            try { disposeMethod.Invoke(maybeDisposable, Array.Empty<object>()); } catch { /* ignore */ }
        }
    }
}
