using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Supplies <see cref="AttrFashionDataReader"/> with the game's per-piece dye-hue rule:
/// <c>Bokura.FashionTableBase[fashionId].ColorGroupId</c> → <c>Bokura.ColorGroupTableBase[group]</c>
/// <c>Type</c> + <c>Hue</c> → <see cref="DyeHuePalette"/>. A Board group (Type 3) passes the wire x
/// through as degrees; any other type treats x as a swatch id (see <see cref="DyeHuePalette"/> for the Lua
/// truth). In release_3.7 every dyeable FashionTable row uses a Board group (or group 0) — Option groups
/// exist in ColorGroupTable but only face/home rows use them — so this is future-proofing plus the
/// group-0 (hue 0) rule, not a fix for any piece the live build ships today.
///
/// <para>
/// Threading: the combat stub probe decodes on the MAIN thread, but the social-data probe decodes on the
/// network I/O thread. Table reflection only ever runs on the main thread (the same rule
/// <see cref="GameDataResonance"/> follows — a lazy <c>GetTable(autoLoad)</c> off-thread can race the
/// game's own load). Off the main thread a cache miss falls back to "x is degrees" (the pre-fix
/// behaviour, correct for every Board piece); once any main-thread decode has resolved a piece, both
/// threads read the cached palette under a short uncontended lock.
/// </para>
///
/// <para>
/// Perf: one reflection resolve per distinct fashion id per session (a few dozen), then a dictionary hit.
/// <see cref="Mapper"/> is a single cached delegate, so decoding allocates nothing extra. Results are
/// cached only once the tables actually returned — a pre-load null is never stuck.
/// </para>
/// </summary>
internal sealed class FashionDyePaletteResolver
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly object[] AutoLoadTrueArgs = { true };

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;
    private readonly int _mainThread = Environment.CurrentManagedThreadId;   // constructed on the main thread (OnHotUpdateReady)
    private readonly object _gate = new();
    private readonly Dictionary<int, DyeHuePalette> _byFashion = new();
    private readonly Dictionary<int, DyeHuePalette> _byGroup = new();
    private bool _warned;

    public FashionDyePaletteResolver(IPluginLog log, IGameTypeRegistry typeRegistry)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        Mapper = MapHue;   // cached once — no delegate allocation per decode
    }

    /// <summary>The mapper to hand to <see cref="AttrFashionDataReader"/> / <see cref="SocialDataReader"/>.</summary>
    public DyeHueMapper Mapper { get; }

    private int MapHue(int fashionId, int x)
    {
        if (fashionId <= 0) return x;
        lock (_gate)
        {
            if (_byFashion.TryGetValue(fashionId, out var hit)) return hit.ToDegrees(x);
        }
        if (Environment.CurrentManagedThreadId != _mainThread) return x;   // never reflect off-thread

        var palette = ResolveFashion(fashionId);
        if (palette is null) return x;   // tables not ready / reflection failed → pre-fix behaviour, retry later
        lock (_gate) _byFashion[fashionId] = palette;
        return palette.ToDegrees(x);
    }

    // FashionTable row → its colour group. Null = can't tell yet (don't cache). A missing fashion row stays
    // on the Board fallback rather than the Lua's hue 0: a lookup miss here is far more likely a reflection
    // shape problem than a real id the client can't resolve, and Board is right for every 3.7 piece.
    private DyeHuePalette? ResolveFashion(int fashionId)
    {
        var row = GetRow("Bokura.FashionTableBase", fashionId, out var tableOk);
        if (!tableOk) return null;
        if (row is null) return DyeHuePalette.Board;
        if (!TryReadInt(row, "ColorGroupId", out var groupId)) return null;
        if (groupId <= 0) return DyeHuePalette.NoGroup;   // Lua: no colour row → h stays 0

        lock (_gate)
        {
            if (_byGroup.TryGetValue(groupId, out var cached)) return cached;
        }
        var palette = ResolveGroup(groupId);
        if (palette is not null) lock (_gate) _byGroup[groupId] = palette;
        return palette;
    }

    private DyeHuePalette? ResolveGroup(int groupId)
    {
        var row = GetRow("Bokura.ColorGroupTableBase", groupId, out var tableOk);
        if (!tableOk) return null;
        if (row is null) return DyeHuePalette.NoGroup;   // Lua: colorRow nil → h stays 0
        if (!TryReadInt(row, "Type", out var type)) return null;
        if (type == DyeHuePalette.BoardType) return DyeHuePalette.Board;

        int[][] hueRows;
        try { hueRows = ReadRows(row.GetType().GetProperty("Hue", AnyInstance)?.GetValue(row)); }
        catch (Exception ex)
        {
            // Unknown Hue shape: settle on the pre-fix behaviour for this group (cached — no retry storm).
            _log.Warning($"[Stellar][Fashion] colour group {groupId} Hue read threw: {ex.GetType().Name}: {ex.Message} — hue left as degrees");
            return DyeHuePalette.Board;
        }
        _log.Info($"[Stellar][Fashion] colour group {groupId} is palette-type (Type={type}, {hueRows.Length} swatches) — dye hues mapped by swatch id");
        return DyeHuePalette.FromGroup(type, hueRows);
    }

    // Table + GetRow(id) via reflection (mirrors GameDataResonance.FetchTable/GetRow). tableOk=false when the
    // table type/handle isn't available yet or anything threw, so the caller retries on a later decode.
    private object? GetRow(string typeName, int id, out bool tableOk)
    {
        tableOk = false;
        try
        {
            var getTable = _typeRegistry.FindType(typeName)?.GetMethod("GetTable", AnyStatic);
            if (getTable is null) return null;
            var table = getTable.GetParameters().Length == 0
                ? getTable.Invoke(null, Array.Empty<object>())
                : getTable.Invoke(null, AutoLoadTrueArgs);
            if (table is null) return null;
            var getRow = table.GetType().GetMethod("GetRow", AnyInstance, binder: null, types: new[] { typeof(int) }, modifiers: null)
                      ?? table.GetType().GetMethod("get_Item", AnyInstance, binder: null, types: new[] { typeof(int) }, modifiers: null);
            if (getRow is null) return null;
            tableOk = true;
            return getRow.Invoke(table, new object[] { id });
        }
        catch (Exception ex)
        {
            tableOk = false;
            if (!_warned) { _warned = true; _log.Warning($"[Stellar][Fashion] {typeName} lookup threw: {ex.GetType().Name}: {ex.Message}"); }
            return null;
        }
    }

    private static bool TryReadInt(object row, string name, out int value)
    {
        value = 0;
        try
        {
            switch (row.GetType().GetProperty(name, AnyInstance)?.GetValue(row))
            {
                case int i: value = i; return true;
                case long l: value = unchecked((int)l); return true;
                default: return false;
            }
        }
        catch { return false; }
    }

    // Bokura.Table.Int32Table (Length + get_Item → Int32Array) — neither implements the BCL IEnumerable
    // (see PandaGameDataProbe.Readers), so walk count + indexer. Also accepts real arrays/enumerables.
    private static int[][] ReadRows(object? table)
    {
        if (table is null) return Array.Empty<int[]>();
        if (table is int[][] jagged) return jagged;
        var n = Count(table);
        if (n <= 0 || n > 4096) return Array.Empty<int[]>();
        var rows = new int[n][];
        for (int i = 0; i < n; i++) rows[i] = ReadInts(Item(table, i));
        return rows;
    }

    private static int[] ReadInts(object? value)
    {
        switch (value)
        {
            case null: return Array.Empty<int>();
            case int[] ints: return ints;
            case IEnumerable e: return e.Cast<object>().Select(Convert.ToInt32).ToArray();
        }
        var n = Count(value);
        if (n <= 0 || n > 4096) return Array.Empty<int>();
        var buf = new int[n];
        for (int i = 0; i < n; i++) buf[i] = Convert.ToInt32(Item(value, i));
        return buf;
    }

    // Length/Count as int. Il2CppInterop can expose both a base and an override "Length" (AmbiguousMatch),
    // so pick the int-typed instance property by scan rather than GetProperty(name).
    private static int Count(object value)
    {
        foreach (var p in value.GetType().GetProperties(AnyInstance))
        {
            if ((p.Name == "Length" || p.Name == "Count") && p.PropertyType == typeof(int) && p.GetIndexParameters().Length == 0)
                return (int)p.GetValue(value)!;
        }
        return 0;
    }

    private static object? Item(object value, int index)
        => value.GetType().GetMethod("get_Item", AnyInstance, binder: null, types: new[] { typeof(int) }, modifiers: null)
               ?.Invoke(value, new object[] { index });
}
