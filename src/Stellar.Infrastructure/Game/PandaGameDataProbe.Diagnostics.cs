using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Diagnostics;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaGameDataProbe
{
    /// <summary>
    /// One-shot diagnostic: dumps the first profession row's projected fields so
    /// the operator can confirm the projection is reading the correct properties
    /// against the live <c>Bokura.ProfessionTableBase</c> shape. No-op unless
    /// <see cref="StellarDiagnostics.IsEnabled"/>.
    /// </summary>
    private void LogFirstProfessionRow(int id, string name, string? iconPath, bool hasSecondary, int[] commonSkillIds)
    {
        if (!StellarDiagnostics.IsEnabled)
        {
            return;
        }

        var skillsSummary = commonSkillIds.Length switch
        {
            0 => "[]",
            _ => $"[{commonSkillIds.Length} ids; first={commonSkillIds[0]}]",
        };

        _log.Info(
            $"[Stellar][GameData][diag] first Profession row: " +
            $"Id={id} Name='{name}' IconPath='{iconPath}' HasSecondary={hasSecondary} CommonSkillIds={skillsSummary}");
    }

    private readonly record struct SkillRowDiagInfo(int Id, string? Name, string? Desc, string? IconPath, int SkillType, int Cooldown, bool IsAoe);

    private void LogFirstSkillRow(SkillRowDiagInfo info)
    {
        if (!StellarDiagnostics.IsEnabled)
        {
            return;
        }

        _log.Info(
            $"[Stellar][GameData][diag] first Skill row: " +
            $"Id={info.Id} Name='{info.Name}' Desc='{Truncate(info.Desc, 60)}' IconPath='{info.IconPath}' " +
            $"SkillType={info.SkillType} Cooldown={info.Cooldown} IsAoe={info.IsAoe}");
    }

    private readonly record struct BuffRowDiagInfo(int Id, string? Name, string? Desc, string? IconPath, int BuffType, bool IsNegative);

    private void LogFirstBuffRow(BuffRowDiagInfo info)
    {
        if (!StellarDiagnostics.IsEnabled)
        {
            return;
        }

        _log.Info(
            $"[Stellar][GameData][diag] first Buff row: " +
            $"Id={info.Id} Name='{info.Name}' Desc='{Truncate(info.Desc, 60)}' IconPath='{info.IconPath}' " +
            $"BuffType={info.BuffType} IsNegative={info.IsNegative}");
    }

    private void LogFirstAttributeRow(int id, string? name, string? shortName, string? iconPath, int category)
    {
        if (!StellarDiagnostics.IsEnabled)
        {
            return;
        }

        _log.Info(
            $"[Stellar][GameData][diag] first Attribute row: " +
            $"Id={id} Name='{name}' ShortName='{shortName}' IconPath='{iconPath}' Category={category}");
    }

    /// <summary>
    /// Phase 6 Iter 1 — one-shot first-row dump for AttributeProfile. Surfaces
    /// the Id, the joined AttrId (links back into AttrDescriptionBase), the
    /// localized Name + TypeDisplayName, and the raw Type classifier int so
    /// the 1/2/3/4/5 → Offensive/Defensive/Support/ElementalAttack/ElementalBonus
    /// mapping can be empirically verified during the in-world scenario.
    /// </summary>
    private void LogFirstAttributeProfileRow(int id, int attrId, string? name, int type, string? typeDisplayName)
    {
        if (!StellarDiagnostics.IsEnabled)
        {
            return;
        }

        _log.Info(
            $"[Stellar][GameData][diag] AttributeProfile first row: " +
            $"Id={id} AttrId={attrId} Name='{name}' Type={type} TypeDisplayName='{typeDisplayName}'");
    }

    private readonly record struct ItemRowDiagInfo(int Id, string? Name, string? Desc, string? IconPath, int Quality, int Type, int GroupId);

    private void LogFirstItemRow(ItemRowDiagInfo info)
    {
        if (!StellarDiagnostics.IsEnabled)
        {
            return;
        }

        _log.Info(
            $"[Stellar][GameData][diag] first Item row: " +
            $"Id={info.Id} Name='{info.Name}' Desc='{Truncate(info.Desc, 60)}' IconPath='{info.IconPath}' " +
            $"Quality={info.Quality} Type={info.Type} GroupId={info.GroupId}");
    }

    /// <summary>
    /// Phase 5 polish — Gap 2 verification. Dumps the first ≤5 non-zero
    /// <c>SkillTableBase.Cooldown</c> values so the unit (ms vs seconds) can be
    /// inferred from value distribution. Values like 4000 / 20000 → ms; values
    /// like 4 / 20 → seconds.
    /// </summary>
    private void LogSkillCooldownSamples(List<(int id, int cooldown)> samples)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (samples.Count == 0)
        {
            _log.Info("[Stellar][GameData][diag] Skill cooldown samples: <none — no non-zero Cooldown columns>");
            return;
        }

        var formatted = string.Join(", ", samples.ConvertAll(s => $"#{s.id}={s.cooldown}"));
        _log.Info($"[Stellar][GameData][diag] Skill cooldown samples (first {samples.Count}): {formatted}");
    }

    /// <summary>
    /// Phase 5 polish — Gap 4 verification. Reports the Type-int distribution
    /// inside the <see cref="Stellar.Abstractions.Domain.GameData.ItemKind.Module"/>
    /// Id range (5_500_000 – 5_509_999) plus the same Type ints' presence outside
    /// the range — used to validate whether the Id-range heuristic is the right
    /// shape, or whether a single Type int identifies modules independent of Id.
    /// </summary>
    private void LogItemModuleRangeDistribution(ModuleRangeStats stats)
    {
        if (!StellarDiagnostics.IsEnabled) return;

        var inRangeSummary = stats.InRangeTypeCounts.Count == 0
            ? "<empty>"
            : string.Join(", ", stats.InRangeTypeCounts.OrderBy(kv => kv.Key)
                .Select(kv => $"Type={kv.Key}:{kv.Value}"));

        _log.Info(
            $"[Stellar][GameData][diag] Item Module-range (5_500_000–5_509_999): " +
            $"count={stats.InRangeCount} types={{{inRangeSummary}}}");

        // For each Type int that appears in-range, report how many out-of-range
        // items share that Type. If a Type is purely in-range → the Type int is
        // the real Module marker (Id range is fallback). If a Type also appears
        // heavily out-of-range → the Type is generic and the Id range is the
        // real Module marker.
        if (stats.InRangeTypeCounts.Count == 0) return;
        foreach (var kv in stats.InRangeTypeCounts.OrderBy(kv => kv.Key))
        {
            var outCount = stats.OutOfRangeTypeCounts.TryGetValue(kv.Key, out var c) ? c : 0;
            _log.Info(
                $"[Stellar][GameData][diag]   Type={kv.Key}: in-range={kv.Value} out-of-range={outCount}");
        }
    }

    /// <summary>
    /// One-shot first-row property-bag dump for Quest. Quest/Award projectors
    /// return 0 rows because the Id column is not literally named <c>Id</c> on
    /// these tables — the dump surfaces every public property (name, type, value)
    /// so the operator can identify the real Id column and the projector fallback
    /// list can be widened. No-op unless <see cref="StellarDiagnostics.IsEnabled"/>.
    /// </summary>
    private void LogFirstQuestRow(object row, Type rowType)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        LogFirstRowPropertyBag("Quest", row, rowType);
    }

    /// <summary>
    /// One-shot first-row property-bag dump for Award. See <see cref="LogFirstQuestRow"/>
    /// for rationale.
    /// </summary>
    private void LogFirstAwardRow(object row, Type rowType)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        LogFirstRowPropertyBag("Award", row, rowType);
    }

    private void LogFirstRowPropertyBag(string label, object row, Type rowType)
    {
        var props = rowType.GetProperties(AnyInstance);
        var parts = new List<string>(props.Length);
        foreach (var p in props)
        {
            if (p.GetIndexParameters().Length != 0) continue;  // skip indexers
            object? raw;
            try { raw = p.GetValue(row); }
            catch { raw = "<throw>"; }
            var typeName = p.PropertyType.Name;
            var valueStr = raw switch
            {
                null => "null",
                string s => $"'{Truncate(s, 40)}'",
                _ => Truncate(raw.ToString(), 40) ?? "null",
            };
            parts.Add($"{p.Name}({typeName})={valueStr}");
        }

        _log.Info(
            $"[Stellar][GameData][diag] {label} first row: " +
            string.Join(" ", parts));
    }

    // ===== Equip first-row dumps ==========================================
    // Unconditional boot one-shots (LogTableShape precedent): each fires once
    // per process from its loader's firstLogged latch. The equip tables'
    // column shapes (nested arrays) were inferred from ZDPS's JSON extraction
    // — these dumps surface the LIVE shapes in a plain scenario log so drift
    // is visible without requiring STELLAR_DIAGNOSTICS.

    private void LogFirstEquipRow(Stellar.Abstractions.Domain.GameData.EquipRowInfo info, Type rowType)
    {
        _log.Info(
            $"[Stellar][GameData][diag] first EquipRow row: " +
            $"Id={info.Id} EquipPart={info.EquipPart} Gs={info.Gs} WearLevel={info.WearLevel} " +
            $"PerfectCap={info.PerfectCap} BasicAttrLibIds={IntsSummary(info.BasicAttrLibIds)} " +
            $"AdvancedAttrLibIds={IntsSummary(info.AdvancedAttrLibIds)} " +
            $"colTypes: WearCondition={PropTypeName(rowType, "WearCondition")} " +
            $"BasicAttrLibId={PropTypeName(rowType, "BasicAttrLibId")}");
    }

    private readonly record struct EquipAttrLibDiagInfo(
        int Id, int AttrLibId, int[][] Effects, int[][] Configs,
        Stellar.Abstractions.Domain.GameData.EquipAttrRange[] Entries);

    private void LogFirstEquipAttrLibRow(EquipAttrLibDiagInfo info, Type rowType)
    {
        var entries = info.Entries;
        var sample = entries.Length > 0
            ? $"(AttrId={entries[0].AttrId} Min={entries[0].Min} Max={entries[0].Max})"
            : "<none>";
        _log.Info(
            $"[Stellar][GameData][diag] first EquipAttrLib row: " +
            $"Id={info.Id} AttrLibId={info.AttrLibId} AttrEffect={Ints2DSummary(info.Effects)} " +
            $"AttrEffectConfig={Ints2DSummary(info.Configs)} entries={entries.Length} first={sample} " +
            $"colTypes: AttrEffect={PropTypeName(rowType, "AttrEffect")} " +
            $"AttrEffectConfig={PropTypeName(rowType, "AttrEffectConfig")} " +
            $"FightValue={PropTypeName(rowType, "FightValue")}");
    }

    // First-row dump for the v2 school lib — confirms the table loaded, columns resolve, and entries
    // parsed (the property bag surfaces a patch-day column rename). Same LogFirst* pattern as every
    // deferred table; gated on STELLAR_DIAGNOSTICS so steady-state pays nothing.
    private readonly record struct SchoolLibDiagInfo(
        int Id, int AttrLibId, int[] AllowParts, int[] TalentSchoolIds,
        Stellar.Abstractions.Domain.GameData.EquipAttrRange[] Entries);

    private void LogFirstSchoolLibRow(SchoolLibDiagInfo info, object row, Type rowType)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var e = info.Entries;
        var sample = e.Length > 0 ? $"(AttrId={e[0].AttrId} {e[0].Min}-{e[0].Max})" : "<none>";
        _log.Info(
            $"[Stellar][GameData][diag] first EquipSchoolAttrLib row: Id={info.Id} AttrLibId={info.AttrLibId} " +
            $"AllowPart={IntsSummary(info.AllowParts)} TalentSchoolId={IntsSummary(info.TalentSchoolIds)} " +
            $"entries={e.Length} first={sample}");
        LogFirstRowPropertyBag("EquipSchoolAttrLib", row, rowType);
    }

    // First-row dump for the gem table — confirms columns resolve (property bag surfaces a rename). Same
    // LogFirst* pattern as every deferred table; gated on STELLAR_DIAGNOSTICS.
    private void LogFirstEnchantItemRow(EnchantItemRowData data, object row, Type rowType)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var eff = data.Effects.Length > 0 ? $"(attr={data.Effects[0].AttrId} val={data.Effects[0].Value})" : "<none>";
        _log.Info($"[Stellar][GameData][diag] first EquipEnchantItem row: gemItemId={data.GemItemId} " +
                  $"typeId={data.TypeId} level={data.Level} effects={data.Effects.Length} first={eff}");
        LogFirstRowPropertyBag("EquipEnchantItem", row, rowType);
    }

    private void LogFirstEquipPartRow(int id, string? name, object row, Type rowType)
    {
        _log.Info($"[Stellar][GameData][diag] first EquipPart row: Id={id} Name='{name}'");
        LogFirstRowPropertyBag("EquipPart", row, rowType);
    }

    private static string PropTypeName(Type rowType, string propertyName)
        => rowType.GetProperty(propertyName, AnyInstance)?.PropertyType.FullName ?? "<missing>";

    private static string IntsSummary(int[] a)
        => a.Length == 0 ? "[]" : $"[{string.Join(",", a)}]";

    private static string Ints2DSummary(int[][] a)
    {
        if (a.Length == 0) return "[]";
        var parts = new List<string>(Math.Min(a.Length, 4));
        for (var i = 0; i < a.Length && i < 4; i++) parts.Add(IntsSummary(a[i]));
        var suffix = a.Length > 4 ? $",… {a.Length} total" : string.Empty;
        return $"[{string.Join(",", parts)}{suffix}]";
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value.Substring(0, max) + "…";
    }

    private readonly HashSet<string> _tableShapeLogged = new();

    /// <summary>
    /// One-shot per-table diagnostic — logs the runtime type of the returned
    /// table object plus a probe of <c>Count</c>/<c>Datas</c>/<c>Values</c>
    /// properties. Always on (unconditional one-shot) because zero-row table
    /// loads need to surface in the log without requiring STELLAR_DIAGNOSTICS.
    /// </summary>
    private void LogTableShape(string tableName, object table)
    {
        if (!_tableShapeLogged.Add(tableName))
        {
            return;
        }

        var t = table.GetType();
        var props = t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                     .Select(p => p.Name)
                     .Where(n => n is "Count" or "Datas" or "Values" or "Keys")
                     .OrderBy(n => n)
                     .ToArray();

        int? count = null;
        try
        {
            var countProp = t.GetProperty("Count", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (countProp is not null && countProp.GetValue(table) is int c) count = c;
        }
        catch { /* ignore */ }

        _log.Info(
            $"[Stellar][GameData][diag] {tableName} table type={t.FullName} count={count?.ToString() ?? "?"} props=[{string.Join(',', props)}]");
    }
}
