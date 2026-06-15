using System;
using System.Collections.Generic;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaGameDataProbe
{
    // ===== Bokura.Table container wrappers (Int32Array / Int32Table) =======
    // Interop shapes (ilspycmd over BepInEx/interop/Panda.Table.dll, v2.11):
    //   Bokura.Table.Int32Array : TableArray<int>
    //       int Length { get; }   (virtual override)
    //       int this[int x]       (get_Item)         + int GetValue(int)
    //   Bokura.Table.Int32Table : TwoDArray<Int32Array, int>
    //       int Length { get; }
    //       Int32Array this[int x] (get_Item)
    // Neither proxy implements the BCL IEnumerable (Il2CppInterop emits only
    // mangled shims like System_Collections_IEnumerable_GetEnumerator), so the
    // enumerable fallbacks in ReadInt32Array never fire for them. These readers
    // walk Length + get_Item via reflection, with the member lookups cached
    // per wrapper Type (~6.3k rows decoded in a single load).

    private readonly record struct WrapperShape(PropertyInfo? Length, MethodInfo? GetItem);

    private static readonly Dictionary<Type, WrapperShape> WrapperShapeCache = new();
    private static readonly HashSet<string> WarnedWrapperTypes = new();

    private static WrapperShape GetWrapperShape(Type type)
    {
        lock (WrapperShapeCache)
        {
            if (WrapperShapeCache.TryGetValue(type, out var cached)) return cached;
            var shape = ResolveWrapperShape(type);
            WrapperShapeCache[type] = shape;
            return shape;
        }
    }

    private static WrapperShape ResolveWrapperShape(Type type)
    {
        PropertyInfo? length;
        try
        {
            length = type.GetProperty("Length", AnyInstance) ?? type.GetProperty("Count", AnyInstance);
        }
        catch (AmbiguousMatchException)
        {
            length = FindIntProperty(type, "Length") ?? FindIntProperty(type, "Count");
        }
        if (length is not null && length.PropertyType != typeof(int))
        {
            length = FindIntProperty(type, length.Name);
        }

        MethodInfo? getItem;
        try
        {
            getItem = type.GetMethod("get_Item", AnyInstance, binder: null, new[] { typeof(int) }, modifiers: null)
                   ?? type.GetMethod("GetValue", AnyInstance, binder: null, new[] { typeof(int) }, modifiers: null);
        }
        catch (AmbiguousMatchException)
        {
            getItem = null;
        }

        return new WrapperShape(length, getItem);
    }

    private static PropertyInfo? FindIntProperty(Type type, string name)
    {
        foreach (var p in type.GetProperties(AnyInstance))
        {
            if (p.Name == name && p.PropertyType == typeof(int) && p.GetIndexParameters().Length == 0)
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>Decodes an Int32Array-style wrapper (Length + get_Item → int) into int[].
    /// Unknown shapes log a one-shot warning naming the type and yield empty.</summary>
    private int[] ReadWrapperInts(object value)
    {
        var shape = GetWrapperShape(value.GetType());
        if (shape.Length is null || shape.GetItem is null)
        {
            WarnUnhandledWrapper(value);
            return Array.Empty<int>();
        }
        try
        {
            var count = shape.Length.GetValue(value) is int n ? n : 0;
            if (count <= 0) return Array.Empty<int>();
            var result = new int[count];
            var args = new object[1];
            for (var i = 0; i < count; i++)
            {
                args[0] = i;
                result[i] = shape.GetItem.Invoke(value, args) switch
                {
                    int v => v,
                    long l => unchecked((int)l),
                    _ => 0,
                };
            }
            return result;
        }
        catch (Exception ex)
        {
            WarnWrapperThrew(value.GetType(), ex);
            return Array.Empty<int>();
        }
    }

    /// <summary>Decodes an Int32Table-style wrapper (Length + get_Item → Int32Array)
    /// into int[][]. Inner elements coerce through int[]/enumerable/wrapper fallbacks.</summary>
    private int[][] ReadWrapperRows(object value)
    {
        var shape = GetWrapperShape(value.GetType());
        if (shape.Length is null || shape.GetItem is null)
        {
            WarnUnhandledWrapper(value);
            return Array.Empty<int[]>();
        }
        try
        {
            var count = shape.Length.GetValue(value) is int n ? n : 0;
            if (count <= 0) return Array.Empty<int[]>();
            var rows = new int[count][];
            var args = new object[1];
            for (var i = 0; i < count; i++)
            {
                args[0] = i;
                rows[i] = shape.GetItem.Invoke(value, args) switch
                {
                    null => Array.Empty<int>(),
                    int[] ints => ints,
                    System.Collections.IEnumerable inner => CollectInts(inner),
                    { } element => ReadWrapperInts(element),
                };
            }
            return rows;
        }
        catch (Exception ex)
        {
            WarnWrapperThrew(value.GetType(), ex);
            return Array.Empty<int[]>();
        }
    }

    /// <summary>
    /// Reads an int-list column whose live shape varies by table: a real int[]
    /// (or any BCL enumerable), an Int32Array wrapper, or an Int32Table whose
    /// first row carries the values (e.g. WearCondition is [[60]]-shaped while
    /// the JSON extraction shows a flat list). Never throws; yields empty.
    /// </summary>
    private int[] ReadIntsFlexible(object row, Type rowType, string propertyName)
    {
        var p = rowType.GetProperty(propertyName, AnyInstance);
        if (p is null) return Array.Empty<int>();
        try
        {
            var v = p.GetValue(row);
            return v switch
            {
                null => Array.Empty<int>(),
                int[] ints => ints,
                long[] longs => ConvertLongs(longs),
                System.Collections.IEnumerable enumerable => CollectInts(enumerable),
                _ => ReadWrapperIntsFlexible(v),
            };
        }
        catch { return Array.Empty<int>(); }
    }

    /// <summary>1D wrapper → as-is; 2D wrapper (get_Item returns a nested array) →
    /// first row only; later rows are dropped — current tables carry a single row;
    /// unknown → one-shot warning + empty.</summary>
    private int[] ReadWrapperIntsFlexible(object value)
    {
        var shape = GetWrapperShape(value.GetType());
        if (shape.GetItem is null)
        {
            WarnUnhandledWrapper(value);
            return Array.Empty<int>();
        }
        if (shape.GetItem.ReturnType == typeof(int)) return ReadWrapperInts(value);
        var rows = ReadWrapperRows(value);
        return rows.Length > 0 ? rows[0] : Array.Empty<int>();
    }

    private void WarnUnhandledWrapper(object value)
    {
        var name = value.GetType().FullName ?? "<unknown>";
        lock (WarnedWrapperTypes)
        {
            if (!WarnedWrapperTypes.Add(name)) return;
        }
        _log.Warning($"[Stellar][GameData] unhandled table-container type '{name}' — column decoded as empty");
    }

    private void WarnWrapperThrew(Type type, Exception ex)
    {
        var key = (type.FullName ?? "<unknown>") + ":threw";
        lock (WarnedWrapperTypes)
        {
            if (!WarnedWrapperTypes.Add(key)) return;
        }
        _log.Warning($"[Stellar][GameData] wrapper read threw for {type.FullName}: {ex.GetType().Name} (one-shot)");
    }
}
