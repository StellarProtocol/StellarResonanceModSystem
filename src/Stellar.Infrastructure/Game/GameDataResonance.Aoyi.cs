using System;
using System.Collections.Generic;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

internal sealed partial class GameDataResonance
{
    // ===== Aoyi summon-skill closure ======================================

    // SkillAoyiTable MonsterId -> aoyi id (e.g. 10084 -> 3944), built once from the live
    // table. Stays null (and is rebuilt on the next lookup) while the table is not yet
    // readable, so a cast arriving before the hot-update tables load cannot freeze an empty
    // index for the session (same hazard class as _skillTableReady). A FETCHED table that
    // enumerates to zero rows is a broken iteration surface, not a load race — after
    // MaxAoyiIndexBuildFailures of those the empty index is cached so negative memoisation
    // resumes and the per-tick render path stops re-running the reflection.
    private Dictionary<int, int>? _aoyiByMonster;
    private int _aoyiIndexBuildFailures;
    private const int MaxAoyiIndexBuildFailures = 3;

    // Aoyi id for a summon/companion skill (0 = none). Clears memoSafe when the answer
    // depended on the not-yet-built index, so the caller skips negative memoisation.
    private int ResolveAoyiForSkill(int skillId, ref bool memoSafe)
    {
        var index = _aoyiByMonster ??= BuildAoyiMonsterIndex();
        if (index is not null) return ImagineAoyiRule.ResolveSummonAoyi(skillId, index);

        // Curated companion rows need no table; only closure answers are index-bound.
        int companionAoyi = ImagineAoyiRule.MapCompanionArcane(skillId);
        if (companionAoyi > 0) return companionAoyi;
        memoSafe = false;
        return 0;
    }

    // Enumerate Bokura.SkillAoyiTableBase and index MonsterId -> row Id. Null = retry later
    // (table not fetchable yet, or an under-budget zero-row enumeration).
    private Dictionary<int, int>? BuildAoyiMonsterIndex()
    {
        var table = FetchTable("Bokura.SkillAoyiTableBase");
        if (table is null) return null;   // hot-update table not loaded yet — no failure charged

        var map = new Dictionary<int, int>(96);
        foreach (var row in CollectTableRows(table))
        {
            var rowType = row.GetType();
            int monsterId = ReadInt(row, rowType, "MonsterId");
            int aoyiId = ReadInt(row, rowType, "Id");
            if (monsterId > 0 && aoyiId > 0) map.TryAdd(monsterId, aoyiId);
        }
        if (map.Count > 0)
        {
            _log.Info($"[Stellar][Resonance] SkillAoyiTable index built: {map.Count} imagines");
            return map;
        }
        if (++_aoyiIndexBuildFailures < MaxAoyiIndexBuildFailures) return null;
        _log.Warning("[Stellar][Resonance] SkillAoyiTable enumerated 0 rows repeatedly; summon-skill resolution disabled this session");
        return map;
    }

    // Walk the ZTable's typed parameterless GetEnumerator() — the only iteration surface that
    // marshals through Il2CppInterop (recon: PandaGameDataProbe.Iteration.cs) — reading each
    // KeyValuePair.Value. Returns the rows it could read; any failure ends the walk.
    private static List<object> CollectTableRows(object table)
    {
        var rows = new List<object>(96);
        MethodInfo? getEnumerator = null;
        foreach (var m in table.GetType().GetMethods(AnyInstance))
        {
            // Two GetEnumerator surfaces exist; the explicit-interface one has a mangled name.
            if (m.Name == "GetEnumerator" && m.GetParameters().Length == 0) { getEnumerator = m; break; }
        }
        object? enumerator;
        try { enumerator = getEnumerator?.Invoke(table, Array.Empty<object>()); }
        catch { return rows; }
        if (enumerator is null) return rows;

        var enumeratorType = enumerator.GetType();
        var moveNext = enumeratorType.GetMethod("MoveNext", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);
        var current = enumeratorType.GetProperty("Current", AnyInstance);
        if (moveNext is not null && current is not null) DrainRows(enumerator, moveNext, current, rows);

        try { enumeratorType.GetMethod("Dispose", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null)?.Invoke(enumerator, Array.Empty<object>()); }
        catch { /* enumerator proxies may not implement Dispose */ }
        return rows;
    }

    // Bounded MoveNext()/Current.Value drain. The cap mirrors PandaGameDataProbe's runaway
    // guard; real Bokura tables are orders of magnitude smaller.
    private static void DrainRows(object enumerator, MethodInfo moveNext, PropertyInfo current, List<object> rows)
    {
        PropertyInfo? valueProperty = null;
        const int safetyCap = 100_000;
        for (var i = 0; i < safetyCap; i++)
        {
            try
            {
                if (!(bool)(moveNext.Invoke(enumerator, Array.Empty<object>()) ?? false)) return;
                if (current.GetValue(enumerator) is not { } kvp) continue;
                valueProperty ??= kvp.GetType().GetProperty("Value", AnyInstance);
                if (valueProperty?.GetValue(kvp) is { } row) rows.Add(row);
            }
            catch
            {
                return;
            }
        }
    }
}
