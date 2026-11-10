using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-based <see cref="IGameDataProbe"/>. Discovers <c>Bokura.*TableBase</c>
/// types in the loaded hot-update assemblies, calls each <c>GetTable(autoLoad: true)</c>
/// to obtain the <c>Bokura.Table.ZTable&lt;K,V&gt;</c> (an Il2CppInterop dictionary
/// proxy), and projects each row into the matching POCO record. See
/// <c>PandaGameDataProbe.Iteration.cs</c> for the row-walk mechanic.
///
/// <para>
/// Iter 2 scope: full eager batch — Profession (via ProfessionSystemTableBase join),
/// Skill, Buff, Attribute (via BasicAttrTableBase + AttrDescriptionBase join), Item.
/// Deferred loads return <c>false</c>; Iter 3 wires the deferred queue.
/// </para>
/// <para>
/// Per-table projection code lives in <c>PandaGameDataProbe.Projections.cs</c>
/// (one private method per eager table + the enum-mapping helpers).
/// </para>
///
/// <para>
/// Recon-during-implementation (Iter 1) findings, noted here for follow-up iters:
/// <list type="bullet">
///   <item><c>Bokura.ProfessionTableBase</c> has <c>Id</c>, <c>ProfessionIcon</c>,
///         <c>CommonSkillIds</c>, <c>HasSecondary</c> — no <c>Name</c>.</item>
///   <item><c>Bokura.NameLanguageTableBase</c> only exposes <c>Region</c> /
///         <c>Language</c> — NOT a join target. The spec asserted otherwise on
///         circumstantial evidence; the real join is via
///         <c>Bokura.ProfessionSystemTableBase</c> which exposes <c>ProfessionId</c>
///         + <c>Name</c> (already-resolved string via <c>ReadProxy.ReadMLString</c>).</item>
///   <item><c>Bokura.Table.ZTable&lt;K,V&gt;</c> is an Il2CppInterop proxy whose
///         BCL <c>IEnumerable</c> shim returns zero rows. Iteration goes through
///         the typed parameterless <c>GetEnumerator()</c> overload (the nested
///         <c>ZTable&lt;K,V&gt;+Enumerator</c> struct). See
///         <c>PandaGameDataProbe.Iteration.cs</c> for the full mechanic.</item>
///   <item><c>GetTable(true)</c> blocks until loaded — single-pass eager batch is
///         sufficient; no retry loop required.</item>
/// </list>
/// </para>
/// </summary>
internal sealed partial class PandaGameDataProbe : IGameDataProbe, IStallSubcategorySource
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly object[] AutoLoadTrueArgs = new object[] { true };

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;
    private readonly PandaMLStringResolver _mlStrings;
    private readonly IClientLanguageInfo _clientLanguage;

    public PandaGameDataProbe(IPluginLog log, IGameTypeRegistry typeRegistry, PandaMLStringResolver mlStrings, IClientLanguageInfo clientLanguage)
    {
        _log = log;
        _typeRegistry = typeRegistry;
        _mlStrings = mlStrings;
        _clientLanguage = clientLanguage;
    }

    /// <summary>
    /// Cheap per-tick readiness probe. Returns true once at least one row has
    /// been deserialized into the ProfessionTableBase static handle — used by
    /// the Host to defer the eager batch until <c>Panda.TableInitUtility</c> has
    /// finished its async load. No-throw; treats any failure as "not ready".
    /// </summary>
    public bool AreEagerTablesReady()
    {
        try
        {
            var t = _typeRegistry.FindType("Bokura.ProfessionTableBase");
            if (t is null) return false;

            var getTable = t.GetMethod("GetTable", AnyStatic);
            if (getTable is null) return false;

            var parameters = getTable.GetParameters();
            var raw = parameters.Length == 0
                ? getTable.Invoke(null, Array.Empty<object>())
                : getTable.Invoke(null, AutoLoadTrueArgs);
            if (raw is null) return false;

            var countProp = raw.GetType().GetProperty("Count", AnyInstance);
            return countProp?.GetValue(raw) is int count && count > 0;
        }
        catch
        {
            return false;
        }
    }

    public bool TryLoadEager(out GameDataEagerSnapshot snapshot)
        => StepEager(long.MaxValue, out snapshot) == GameDataStep.Done;

    public bool TryLoadOne(GameDataTableKind kind, out object cache)
        => StepOne(kind, long.MaxValue, out cache) == GameDataStep.Done;

    // Time-sliced queue drivers (StepEager / StepOne) live in PandaGameDataProbe.SlicedQueue.cs.

    // Per-kind deferred job factory. Each loader lives in one of the PandaGameDataProbe.Deferred*.cs or
    // Projections.Equip.cs sibling partials and runs through the shared DeferredJob<TInfo> envelope, which
    // logs success and swallows failures (logs once, keeps the rows so far). Null = unknown kind.
    private TableLoadJob? CreateDeferredJob(GameDataTableKind kind) => kind switch
    {
        GameDataTableKind.Talent             => LoadTalents(),
        GameDataTableKind.DamageAttr         => LoadDamageAttrs(),
        GameDataTableKind.Equip              => LoadEquips(),
        GameDataTableKind.Weapon             => LoadWeapons(),
        GameDataTableKind.Monster            => LoadMonsters(),
        GameDataTableKind.Npc                => LoadNpcs(),
        GameDataTableKind.Scene              => LoadScenes(),
        GameDataTableKind.Map                => LoadMaps(),
        GameDataTableKind.Quest              => LoadQuests(),
        GameDataTableKind.Dungeon            => LoadDungeons(),
        GameDataTableKind.Activity           => LoadActivities(),
        GameDataTableKind.Achievement        => LoadAchievements(),
        GameDataTableKind.Title              => LoadTitles(),
        GameDataTableKind.Award              => LoadAwards(),
        GameDataTableKind.EquipRow           => LoadEquipRows(),
        GameDataTableKind.EquipAttrLib       => LoadEquipAttrLibs(),
        GameDataTableKind.EquipSchoolAttrLib => LoadEquipSchoolAttrLibs(),
        GameDataTableKind.EquipEnchantItem   => LoadEquipEnchantItems(),
        GameDataTableKind.EquipPart          => LoadEquipSlotNames(),
        _ => null,
    };

    private bool TryGetTable(Type tableBaseType, out object? table)
    {
        table = null;
        try
        {
            var getTable = tableBaseType.GetMethod("GetTable", AnyStatic);
            if (getTable is null)
            {
                _log.Warning($"[Stellar][GameData] {tableBaseType.FullName}.GetTable not found");
                return false;
            }

            // GetTable(bool AutoLoad). autoLoad=true blocks until rows are deserialized.
            var parameters = getTable.GetParameters();
            object? raw = parameters.Length switch
            {
                0 => getTable.Invoke(null, Array.Empty<object>()),
                _ => getTable.Invoke(null, AutoLoadTrueArgs),
            };
            if (raw is null)
            {
                _log.Warning($"[Stellar][GameData] {tableBaseType.FullName}.GetTable returned null");
                return false;
            }
            table = raw;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"[Stellar][GameData] GetTable threw for {tableBaseType.FullName}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static int ReadInt(object row, Type rowType, string propertyName)
    {
        var p = rowType.GetProperty(propertyName, AnyInstance);
        if (p is null) return 0;
        try
        {
            var v = p.GetValue(row);
            return v switch
            {
                int i => i,
                long l => unchecked((int)l),
                uint u => unchecked((int)u),
                short s => s,
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
            var v = p.GetValue(row);
            return v switch
            {
                long l => l,
                int i => i,
                uint u => u,
                _ => 0,
            };
        }
        catch { return 0; }
    }

    private static uint ReadUInt(object row, Type rowType, string propertyName)
    {
        var p = rowType.GetProperty(propertyName, AnyInstance);
        if (p is null) return 0u;
        try
        {
            var v = p.GetValue(row);
            return v switch
            {
                uint u => u,
                int i => unchecked((uint)i),
                long l => unchecked((uint)l),
                _ => 0u,
            };
        }
        catch { return 0u; }
    }

    private static bool ReadBool(object row, Type rowType, string propertyName)
    {
        var p = rowType.GetProperty(propertyName, AnyInstance);
        if (p is null) return false;
        try { return p.GetValue(row) is true; } catch { return false; }
    }

    private string ReadString(object row, Type rowType, string propertyName)
    {
        var p = rowType.GetProperty(propertyName, AnyInstance);
        if (p is null) return string.Empty;
        try
        {
            var v = p.GetValue(row);
            return v as string ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Read a property that may already be a resolved <see cref="string"/> (the
    /// common case — <c>ReadProxy.ReadMLString</c> resolves at row-read time) or,
    /// rarely, a raw MLString handle that needs <see cref="PandaMLStringResolver"/>.
    /// </summary>
    private string ReadStringOrMlString(object row, Type rowType, string propertyName)
    {
        var p = rowType.GetProperty(propertyName, AnyInstance);
        if (p is null) return string.Empty;
        try
        {
            var v = p.GetValue(row);
            return v switch
            {
                null => string.Empty,
                string s => s,
                _ => _mlStrings.Resolve(v),
            };
        }
        catch { return string.Empty; }
    }

    private int[] ReadInt32Array(object row, Type rowType, string propertyName)
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
                // Bokura.Table.Int32Array proxy (no BCL IEnumerable) — see Readers partial.
                _ => ReadWrapperInts(v),
            };
        }
        catch { return Array.Empty<int>(); }
    }

    /// <summary>
    /// Reads a nested int-array column (<c>int[][]</c> on the JSON side; an
    /// Il2Cpp reference-array of int-arrays on the live side). Mirrors
    /// <see cref="ReadInt32Array"/> one level deeper: any outer enumerable is
    /// walked, and each element is coerced through the same int[]/long[]/
    /// enumerable fallbacks as the 1D reader. Never throws; missing or
    /// unreadable columns yield an empty outer array.
    /// </summary>
    private int[][] ReadInt32Array2D(object row, Type rowType, string propertyName)
    {
        var p = rowType.GetProperty(propertyName, AnyInstance);
        if (p is null) return Array.Empty<int[]>();
        try
        {
            var v = p.GetValue(row);
            return v switch
            {
                null => Array.Empty<int[]>(),
                int[][] jagged => jagged,
                System.Collections.IEnumerable outer => CollectIntArrays(outer),
                // Bokura.Table.Int32Table proxy (no BCL IEnumerable) — see Readers partial.
                _ => ReadWrapperRows(v),
            };
        }
        catch { return Array.Empty<int[]>(); }
    }

    private int[][] CollectIntArrays(System.Collections.IEnumerable outer)
    {
        var buffer = new List<int[]>(8);
        foreach (var item in outer)
        {
            buffer.Add(item switch
            {
                null => Array.Empty<int>(),
                int[] ints => ints,
                long[] longs => ConvertLongs(longs),
                System.Collections.IEnumerable inner => CollectInts(inner),
                _ => ReadWrapperInts(item),
            });
        }
        return buffer.ToArray();
    }

    private static int[] ConvertLongs(long[] longs)
    {
        var result = new int[longs.Length];
        for (var i = 0; i < longs.Length; i++)
        {
            result[i] = unchecked((int)longs[i]);
        }
        return result;
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
                _ => 0,
            });
        }
        return buffer.ToArray();
    }
}
