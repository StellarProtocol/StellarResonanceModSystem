using System;
using System.Collections.Generic;

namespace Stellar.Infrastructure.Game;

internal sealed partial class GameDataResonance
{
    // ===== Row field readers (mirror PandaGameDataProbe.cs) ===============

    private static int ReadInt(object row, Type rowType, string propertyName)
    {
        var p = rowType.GetProperty(propertyName, AnyInstance);
        if (p is null) return 0;
        try
        {
            return p.GetValue(row) switch
            {
                int i => i,
                long l => unchecked((int)l),
                uint u => unchecked((int)u),
                short s => s,
                float f => (int)f,
                _ => 0,
            };
        }
        catch { return 0; }
    }

    private static long ReadLong(object row, Type rowType, string propertyName)
    {
        var p = rowType.GetProperty(propertyName, AnyInstance);
        if (p is null) return 0;
        try
        {
            return p.GetValue(row) switch
            {
                long l => l,
                int i => i,
                uint u => u,
                _ => 0,
            };
        }
        catch { return 0; }
    }

    private static float ReadFloat(object row, Type rowType, string propertyName)
    {
        var p = rowType.GetProperty(propertyName, AnyInstance);
        if (p is null) return 0f;
        try
        {
            return p.GetValue(row) switch
            {
                float f => f,
                double d => (float)d,
                int i => i,
                long l => l,
                _ => 0f,
            };
        }
        catch { return 0f; }
    }

    private static string ReadString(object row, Type rowType, string propertyName)
    {
        var p = rowType.GetProperty(propertyName, AnyInstance);
        if (p is null) return string.Empty;
        try { return p.GetValue(row) as string ?? string.Empty; }
        catch { return string.Empty; }
    }

    // Resolved string in the common case; raw MLString handle resolved via the
    // shared PandaMLStringResolver otherwise (mirrors the probe).
    private string ReadStringOrMl(object row, Type rowType, string propertyName)
    {
        var p = rowType.GetProperty(propertyName, AnyInstance);
        if (p is null) return string.Empty;
        try
        {
            return p.GetValue(row) switch
            {
                null => string.Empty,
                string s => s,
                var v => _mlStrings.Resolve(v),
            };
        }
        catch { return string.Empty; }
    }

    private static int[] ReadIntArray(object row, Type rowType, string propertyName)
    {
        var p = rowType.GetProperty(propertyName, AnyInstance);
        if (p is null) return Array.Empty<int>();
        try
        {
            return p.GetValue(row) switch
            {
                null => Array.Empty<int>(),
                int[] ints => ints,
                System.Collections.IEnumerable e => CollectInts(e),
                // The game's generated collections (e.g. Bokura.Table.Int32Array, used by SlotPositionId /
                // EffectIDs) do NOT implement the BCL IEnumerable — read them via Count + get_Item(int).
                var v => CollectViaIndexer(v),
            };
        }
        catch { return Array.Empty<int>(); }
    }

    // Read ints from a custom collection that exposes Count/Length + get_Item(int) but not BCL IEnumerable.
    private static int[] CollectViaIndexer(object value)
    {
        var t = value.GetType();
        var countProp = t.GetProperty("Count", AnyInstance) ?? t.GetProperty("Length", AnyInstance);
        var indexer = t.GetMethod("get_Item", AnyInstance, binder: null, types: new[] { typeof(int) }, modifiers: null);
        if (countProp is null || indexer is null) return Array.Empty<int>();
        try
        {
            int count = Convert.ToInt32(countProp.GetValue(value));
            if (count <= 0 || count > 4096) return Array.Empty<int>();
            var buf = new int[count];
            var arg = new object[1];
            for (int i = 0; i < count; i++) { arg[0] = i; buf[i] = Convert.ToInt32(indexer.Invoke(value, arg)); }
            return buf;
        }
        catch { return Array.Empty<int>(); }
    }

    private static int[] CollectInts(System.Collections.IEnumerable enumerable)
    {
        var buffer = new List<int>(8);
        foreach (var item in enumerable)
        {
            buffer.Add(item switch
            {
                int i => i,
                long l => unchecked((int)l),
                uint u => unchecked((int)u),
                _ => 0,
            });
        }
        return buffer.ToArray();
    }
}
