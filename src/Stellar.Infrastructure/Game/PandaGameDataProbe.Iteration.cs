using System;
using System.Collections.Generic;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaGameDataProbe
{
    /// <summary>
    /// Iterate rows of a <c>Bokura.Table.ZTable&lt;K, V&gt;</c> as projected values.
    ///
    /// <para>
    /// Recon (Iter 1b) — <c>ZTable&lt;K,V&gt;</c> is an Il2CppInterop proxy that wraps
    /// the IL2CPP <c>Il2CppSystem.Collections.Generic.Dictionary&lt;long, Object&gt;</c>
    /// (keys live as <c>long</c> internally; the public <c>K</c> is converted via
    /// <c>ZTableUtils.ConvertToLong</c>). Three iteration surfaces exist:
    /// <list type="bullet">
    ///   <item><c>System_Collections_IEnumerable_GetEnumerator()</c> — returns
    ///         <c>Il2CppSystem.Collections.IEnumerator</c>; BCL <c>foreach</c> sees
    ///         zero entries because Il2CppInterop's non-generic shim doesn't
    ///         marshal back to a BCL <c>IEnumerable</c>.</item>
    ///   <item><c>get_Datas()</c> + <c>get_Keys()</c>/<c>get_Values()</c> — all
    ///         return Il2Cpp collections that suffer the same problem.</item>
    ///   <item><b>The typed <c>GetEnumerator()</c></b> returns the nested
    ///         <c>ZTable&lt;K,V&gt;+Enumerator</c> struct (a real Il2CppInterop
    ///         proxy with concrete <c>MoveNext()</c> / <c>get_Current()</c> methods
    ///         that DO marshal correctly because the return type is the typed
    ///         <c>KeyValuePair&lt;K,V&gt;</c> proxy). This is the reliable path.</item>
    /// </list>
    /// We resolve the typed <c>GetEnumerator()</c> (the parameterless overload that
    /// is NOT the explicit <c>IEnumerable</c> implementation) and walk it via
    /// <c>MoveNext()</c>/<c>Current.Value</c>. A defensive fallback walks
    /// <c>get_Item(K)</c> over an integer key span when the enumerator yields zero
    /// rows on an apparently-non-empty table (<c>Count &gt; 0</c>).
    /// </para>
    /// </summary>
    private IEnumerable<object?> EnumerateRowsAsObjects(object table)
    {
        var rows = CollectRowsViaTypedEnumerator(table);

        // Fallback: if the enumerator yielded nothing but Count > 0, walk dense
        // integer keys (Profession is dense, ~11 ids). Bounded by reported Count
        // + safety margin so a sparse table doesn't spin forever.
        if (rows.Count == 0 && TryGetReportedCount(table, out var reportedCount) && reportedCount > 0)
        {
            _log.Warning(
                $"[Stellar][GameData] typed-enumerator yielded 0 rows on {table.GetType().Name} (Count={reportedCount}); " +
                "falling back to dense-key TryGetValue scan");
            rows = CollectRowsViaDenseKeyScan(table, reportedCount);
        }

        foreach (var row in rows)
        {
            yield return row;
        }
    }

    /// <summary>
    /// Walk the typed parameterless <c>GetEnumerator()</c> overload on the ZTable
    /// proxy. The returned <c>Enumerator</c> struct is an Il2CppInterop wrapper
    /// whose <c>Current</c> is a typed <c>KeyValuePair&lt;K, V&gt;</c> proxy —
    /// reading <c>.Value</c> via reflection yields the row object.
    /// </summary>
    private List<object?> CollectRowsViaTypedEnumerator(object table)
    {
        var rows = new List<object?>(capacity: 16);
        var tableType = table.GetType();

        var typedGetEnumerator = FindTypedGetEnumerator(tableType);
        if (typedGetEnumerator is null)
        {
            return rows;
        }

        if (!TryInvokeGetEnumerator(typedGetEnumerator, table, tableType.Name, out var enumerator) || enumerator is null)
        {
            return rows;
        }

        var enumeratorType = enumerator.GetType();
        var moveNext = enumeratorType.GetMethod("MoveNext", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);
        var getCurrent = enumeratorType.GetProperty("Current", AnyInstance);
        if (moveNext is null || getCurrent is null)
        {
            return rows;
        }

        DrainEnumeratorRows(enumerator, moveNext, getCurrent, rows);
        TryDispose(enumerator);
        return rows;
    }

    /// <summary>
    /// Disambiguate: there are two GetEnumerator methods on the proxy:
    ///   1. parameterless typed -> nested Enumerator (the one we want)
    ///   2. explicit IEnumerable.GetEnumerator() with a mangled IL2CPP name
    /// Filter by exact name + zero params.
    /// </summary>
    private static MethodInfo? FindTypedGetEnumerator(Type tableType)
    {
        foreach (var m in tableType.GetMethods(AnyInstance))
        {
            if (m.Name != "GetEnumerator") continue;
            if (m.GetParameters().Length != 0) continue;
            return m;
        }
        return null;
    }

    /// <summary>
    /// Invoke the resolved typed <c>GetEnumerator</c>, catching and logging any
    /// exception. Returns <c>false</c> (and sets <paramref name="enumerator"/> to
    /// <c>null</c>) if the invocation throws.
    /// </summary>
    private bool TryInvokeGetEnumerator(MethodInfo method, object table, string typeName, out object? enumerator)
    {
        try
        {
            enumerator = method.Invoke(table, Array.Empty<object>());
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"[Stellar][GameData] GetEnumerator threw on {typeName}: {ex.GetType().Name}: {ex.Message}");
            enumerator = null;
            return false;
        }
    }

    /// <summary>
    /// Core iteration loop. Reads each <c>KeyValuePair&lt;K,V&gt;.Value</c> from the
    /// enumerator proxy and appends each row object to <paramref name="rows"/>. A
    /// hard cap of 100 000 iterations guards against runaway enumerator proxy bugs.
    /// </summary>
    private static void DrainEnumeratorRows(
        object enumerator,
        MethodInfo moveNext,
        PropertyInfo getCurrent,
        List<object?> rows)
    {
        // KeyValuePair<K, V>.Value reader — resolved once on first non-null Current.
        PropertyInfo? kvpValueProp = null;

        // Hard cap to defeat a runaway enumerator (proxy bug). Real Bokura tables
        // are bounded; 100k is several orders of magnitude past any of them.
        const int safetyCap = 100_000;
        for (var i = 0; i < safetyCap; i++)
        {
            bool advanced;
            try
            {
                advanced = (bool)(moveNext.Invoke(enumerator, Array.Empty<object>()) ?? false);
            }
            catch
            {
                break;
            }
            if (!advanced) break;

            AppendCurrentRow(enumerator, getCurrent, ref kvpValueProp, rows);
        }
    }

    /// <summary>
    /// Read one <c>Current</c> from the enumerator proxy and append the row value
    /// to <paramref name="rows"/>. Resolves <c>kvpValueProp</c> lazily on first
    /// non-null <c>Current</c>; falls back to emitting <c>Current</c> directly when
    /// the row is not a <c>KeyValuePair</c> shape.
    /// </summary>
    private static void AppendCurrentRow(
        object enumerator,
        PropertyInfo getCurrent,
        ref PropertyInfo? kvpValueProp,
        List<object?> rows)
    {
        object? current;
        try
        {
            current = getCurrent.GetValue(enumerator);
        }
        catch
        {
            return;
        }
        if (current is null)
        {
            rows.Add(null);
            return;
        }

        if (kvpValueProp is null)
        {
            kvpValueProp = current.GetType().GetProperty("Value", AnyInstance);
        }

        if (kvpValueProp is null)
        {
            // Not a KeyValuePair shape after all — emit the current as the row.
            rows.Add(current);
            return;
        }

        try { rows.Add(kvpValueProp.GetValue(current)); }
        catch { rows.Add(null); }
    }

    /// <summary>
    /// Key-aware variant of <see cref="CollectRowsViaTypedEnumerator"/> for ZTables whose row VALUE
    /// does not carry its own id — the id lives only in the dictionary KEY (e.g.
    /// <c>ZTable&lt;int, StallDetailTableBase&gt;</c>, where the row exposes only Category/Subcategory).
    /// Walks the same typed enumerator but reads BOTH <c>KeyValuePair.Key</c> and <c>.Value</c>. The
    /// dense-key fallback used by the value-only path is deliberately NOT applied here: item-id-keyed
    /// tables are large and sparse (keys ~1_010_011+), so a 1..N scan can't reach them.
    /// </summary>
    private List<(object? key, object? value)> CollectKeyedRowsViaTypedEnumerator(object table)
    {
        var rows = new List<(object?, object?)>(capacity: 1024);
        var tableType = table.GetType();

        var typedGetEnumerator = FindTypedGetEnumerator(tableType);
        if (typedGetEnumerator is null) return rows;

        if (!TryInvokeGetEnumerator(typedGetEnumerator, table, tableType.Name, out var enumerator) || enumerator is null)
        {
            return rows;
        }

        var enumeratorType = enumerator.GetType();
        var moveNext = enumeratorType.GetMethod("MoveNext", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);
        var getCurrent = enumeratorType.GetProperty("Current", AnyInstance);
        if (moveNext is null || getCurrent is null) return rows;

        PropertyInfo? keyProp = null;
        PropertyInfo? valProp = null;
        const int safetyCap = 100_000;
        for (var i = 0; i < safetyCap; i++)
        {
            bool advanced;
            try { advanced = (bool)(moveNext.Invoke(enumerator, Array.Empty<object>()) ?? false); }
            catch { break; }
            if (!advanced) break;

            object? current;
            try { current = getCurrent.GetValue(enumerator); }
            catch { continue; }
            if (current is null) continue;

            if (keyProp is null)
            {
                keyProp = current.GetType().GetProperty("Key", AnyInstance);
                valProp = current.GetType().GetProperty("Value", AnyInstance);
            }
            if (keyProp is null || valProp is null) continue;

            try { rows.Add((keyProp.GetValue(current), valProp.GetValue(current))); }
            catch { /* skip this row */ }
        }

        TryDispose(enumerator);
        return rows;
    }

    /// <summary>
    /// Dense-key fallback. Profession is densely keyed (~11 ids, 1..N). Walk a
    /// bounded integer range and call <c>get_Item(K)</c>; swallow lookup failures.
    /// Caps at <c>reportedCount * 4 + 64</c> to bound a sparse-table fallback.
    /// </summary>
    private List<object?> CollectRowsViaDenseKeyScan(object table, int reportedCount)
    {
        var rows = new List<object?>(capacity: reportedCount);
        var tableType = table.GetType();

        var indexer = tableType.GetMethod("get_Item", AnyInstance);
        var containsKey = tableType.GetMethod("ContainsKey", AnyInstance);
        if (indexer is null) return rows;

        var cap = Math.Min(reportedCount * 4 + 64, 100_000);
        var keyArg = new object[1];
        for (var id = 1; id <= cap && rows.Count < reportedCount; id++)
        {
            keyArg[0] = id;

            // Probe with ContainsKey first if available — avoids exception flood
            // on missing keys (sparse tables would otherwise throw per id).
            if (containsKey is not null)
            {
                try
                {
                    if (containsKey.Invoke(table, keyArg) is not true) continue;
                }
                catch { continue; }
            }

            object? row;
            try { row = indexer.Invoke(table, keyArg); }
            catch { continue; }
            if (row is not null) rows.Add(row);
        }

        return rows;
    }

    private static bool TryGetReportedCount(object table, out int count)
    {
        count = 0;
        try
        {
            var p = table.GetType().GetProperty("Count", AnyInstance);
            if (p?.GetValue(table) is int c)
            {
                count = c;
                return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    private static void TryDispose(object? maybeDisposable)
    {
        if (maybeDisposable is IDisposable d)
        {
            try { d.Dispose(); } catch { /* ignore */ }
            return;
        }
        // The Il2Cpp Enumerator exposes Dispose() but doesn't implement BCL IDisposable.
        var disposeMethod = maybeDisposable?.GetType().GetMethod("Dispose", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);
        if (disposeMethod is not null)
        {
            try { disposeMethod.Invoke(maybeDisposable, Array.Empty<object>()); } catch { /* ignore */ }
        }
    }
}
