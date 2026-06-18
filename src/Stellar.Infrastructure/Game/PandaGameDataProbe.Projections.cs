using System;
using System.Collections.Generic;
using System.Diagnostics;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaGameDataProbe
{
    /// <summary>
    /// Shared eager-table-load envelope. Resolves the type, fetches the ZTable,
    /// times the walk, logs the per-table summary, swallows exceptions (logs
    /// once, returns the partially-built dictionary). Each per-table loader
    /// only supplies a label, a type name, and a projector that turns one row
    /// object into a key + value pair.
    /// </summary>
    /// <param name="label">Human-readable label for log lines (e.g. "Skill").</param>
    /// <param name="typeName">Fully-qualified Bokura table type (e.g. "Bokura.SkillTableBase").</param>
    /// <param name="capacityHint">Pre-allocation hint for the result dictionary.</param>
    /// <param name="projector">Per-row projector. Returning <c>id == 0</c> skips the row.</param>
    private IReadOnlyDictionary<int, TInfo> LoadEagerTable<TInfo>(
        string label,
        string typeName,
        int capacityHint,
        Func<object, Type, (int id, TInfo info)> projector)
        where TInfo : struct
        => LoadTableCore<TInfo>(batch: "eager", label, typeName, capacityHint, projector);

    /// <summary>
    /// Shared deferred-table-load envelope — identical to <see cref="LoadEagerTable"/>
    /// but emits an <c>[GameData] deferred: …</c> log line on success. Returns
    /// the built dictionary (possibly empty); callers pass it through TryLoadOne
    /// as the boxed cache.
    /// </summary>
    private IReadOnlyDictionary<int, TInfo> LoadDeferredTable<TInfo>(
        string label,
        string typeName,
        int capacityHint,
        Func<object, Type, (int id, TInfo info)> projector)
        where TInfo : struct
        => LoadTableCore<TInfo>(batch: "deferred", label, typeName, capacityHint, projector);

    private IReadOnlyDictionary<int, TInfo> LoadTableCore<TInfo>(
        string batch,
        string label,
        string typeName,
        int capacityHint,
        Func<object, Type, (int id, TInfo info)> projector)
        where TInfo : struct
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new Dictionary<int, TInfo>(capacity: capacityHint);

        try
        {
            var tableType = _typeRegistry.FindType(typeName);
            if (tableType is null)
            {
                _log.Warning($"[Stellar][GameData] missing type {typeName}");
                return result;
            }

            if (!TryGetTable(tableType, out var table) || table is null)
            {
                return result;
            }

            LogTableShape(label + "TableBase", table);

            foreach (var row in EnumerateRowsAsObjects(table))
            {
                if (row is null) continue;
                var (id, info) = projector(row, row.GetType());
                if (id == 0) continue;
                result[id] = info;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[Stellar][GameData] Load{label} threw: {ex.GetType().Name}: {ex.Message}");
        }

        stopwatch.Stop();
        _log.Info($"[Stellar][GameData] {batch}: {label} loaded ({result.Count} rows, {stopwatch.ElapsedMilliseconds}ms)");
        return result;
    }

    // ===== Profession =====================================================

    /// <summary>
    /// Build the Profession lookup. ProfessionTableBase has no Name field —
    /// names live on the sibling ProfessionSystemTableBase (joined by
    /// ProfessionId). On any failure log once and return an empty dictionary —
    /// never throw.
    /// </summary>
    private IReadOnlyDictionary<int, ProfessionInfo> LoadProfessions()
    {
        var nameByProfessionId = BuildProfessionNameLookup();
        var firstLogged = false;

        return LoadEagerTable<ProfessionInfo>(
            label: "Profession",
            typeName: "Bokura.ProfessionTableBase",
            capacityHint: 16,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var iconPath = ReadString(row, rowType, "ProfessionIcon");
                var hasSecondary = ReadBool(row, rowType, "HasSecondary");
                var commonSkillIds = ReadInt32Array(row, rowType, "CommonSkillIds");
                nameByProfessionId.TryGetValue(id, out var name);
                name ??= string.Empty;

                if (!firstLogged)
                {
                    firstLogged = true;
                    LogFirstProfessionRow(id, name, iconPath, hasSecondary, commonSkillIds);
                }

                return (id, new ProfessionInfo(
                    Id: id,
                    Name: name,
                    IconPath: iconPath ?? string.Empty,
                    HasSecondary: hasSecondary,
                    CommonSkillIds: commonSkillIds));
            });
    }

    private Dictionary<int, string> BuildProfessionNameLookup()
    {
        var lookup = new Dictionary<int, string>(capacity: 16);

        var systemType = _typeRegistry.FindType("Bokura.ProfessionSystemTableBase");
        if (systemType is null)
        {
            _log.Warning("[Stellar][GameData] missing type Bokura.ProfessionSystemTableBase; profession names will be empty");
            return lookup;
        }

        if (!TryGetTable(systemType, out var table) || table is null)
        {
            return lookup;
        }

        LogTableShape("ProfessionSystemTableBase", table);

        foreach (var row in EnumerateRowsAsObjects(table))
        {
            if (row is null) continue;
            var rowType = row.GetType();
            var professionId = ReadInt(row, rowType, "ProfessionId");
            if (professionId == 0) continue;
            if (lookup.ContainsKey(professionId)) continue;

            var nameRaw = ReadStringOrMlString(row, rowType, "Name");
            if (!string.IsNullOrEmpty(nameRaw))
            {
                lookup[professionId] = nameRaw;
            }
        }

        return lookup;
    }

    // ===== Skill ==========================================================

    /// <summary>
    /// Build the Skill lookup from <c>Bokura.SkillTableBase</c>. Recon (Phase 5
    /// polish) re-confirmed the row schema: <c>Id, Name, Desc, Icon (NOT
    /// IconPath), SkillType (Int32), IsAoe (Boolean), CoolTimeType (Int32)</c>.
    /// There is NO <c>Cooldown</c> int column on this build — <see cref="SkillInfo.CooldownMs"/>
    /// is left at 0 in v1. Recon found <c>CoolTimeType</c> (an index into a
    /// separate cooldown-by-type / -by-level table) and <c>EnergyChargeTime</c>
    /// (Int64, charge-up duration, not a cooldown). Surfacing real cooldown
    /// values requires resolving the indirect lookup — deferred to Phase 7.
    /// The diagnostic <see cref="LogSkillCooldownSamples"/> remains as a
    /// confirmation that no direct column exists.
    /// </summary>
    private IReadOnlyDictionary<int, SkillInfo> LoadSkills()
    {
        var firstLogged = false;
        var cooldownSamples = new List<(int id, int cooldown)>(8);

        var result = LoadEagerTable<SkillInfo>(
            label: "Skill",
            typeName: "Bokura.SkillTableBase",
            capacityHint: 256,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var info = ReadSkillRowFields(row, rowType);

                if (!firstLogged)
                {
                    firstLogged = true;
                    LogFirstSkillRow(new SkillRowDiagInfo(id, info.name, info.desc, info.iconPath, info.skillTypeInt, info.cooldown, info.isAoe));
                }
                if (info.cooldown > 0 && cooldownSamples.Count < 5)
                {
                    cooldownSamples.Add((id, info.cooldown));
                }

                return (id, new SkillInfo(
                    Id: id,
                    Name: info.name ?? string.Empty,
                    Description: info.desc ?? string.Empty,
                    IconPath: info.iconPath ?? string.Empty,
                    Kind: MapSkillKind(info.skillTypeInt),
                    CooldownMs: info.cooldown,
                    IsAoe: info.isAoe));
            });

        LogSkillCooldownSamples(cooldownSamples);
        return result;
    }

    /// <summary>
    /// Read all non-Id fields from one <c>SkillTableBase</c> row. Recon (Phase 5
    /// polish) re-confirmed: column is <c>Icon</c> (not <c>IconPath</c>); no direct
    /// <c>Cooldown</c> int column on this build — both names are probed defensively
    /// so a future patch surfaces the value without code change.
    /// </summary>
    private (string? name, string? desc, string? iconPath, int skillTypeInt, int cooldown, bool isAoe)
        ReadSkillRowFields(object row, Type rowType)
    {
        var name = ReadStringOrMlString(row, rowType, "Name");
        // Internal/system skills (field markers, dodge/attack variants) carry an EMPTY localized `Name` in this
        // client — only `NameDesign` (the Chinese design label) is populated. Locale-gate the fallback: use
        // NameDesign only on a Chinese client; on English resolve a curated override or an id-based label, never
        // Chinese. Flows to every consumer (CombatMeter skill breakdown, Entity Inspector) via GetSkill().Name.
        if (string.IsNullOrEmpty(name))
        {
            var id = ReadInt(row, rowType, "Id");
            var nameDesign = ReadStringOrMlString(row, rowType, "NameDesign");
            name = ResolveEmptyName("Skill", id, nameDesign);
        }
        var desc = ReadStringOrMlString(row, rowType, "Desc");
        if (string.IsNullOrEmpty(desc))
        {
            desc = ReadStringOrMlString(row, rowType, "Description");
        }
        // SkillTableBase column is `Icon` (not `IconPath`).
        var iconPath = ReadString(row, rowType, "Icon");
        if (string.IsNullOrEmpty(iconPath))
        {
            iconPath = ReadString(row, rowType, "IconPath");
        }
        var skillTypeInt = ReadInt(row, rowType, "SkillType");
        // No direct Cooldown int column on this build; left at 0. Try
        // both names defensively so a future patch that adds Cooldown
        // surfaces it without code change.
        var cooldown = ReadInt(row, rowType, "Cooldown");
        if (cooldown == 0)
        {
            cooldown = ReadInt(row, rowType, "CooldownMs");
        }
        var isAoe = ReadBool(row, rowType, "IsAoe");
        return (name, desc, iconPath, skillTypeInt, cooldown, isAoe);
    }

    // ===== Skill leveled-id -> base-id map =================================

    private readonly record struct BaseSkillRef(int BaseSkillId);   // LoadEagerTable needs a struct TInfo

    /// <summary>
    /// Build the leveled-skill-id → base-skill-id map from
    /// <c>Bokura.SkillFightLevelTableBase</c>. Each row's key (<c>Id</c>, a
    /// <c>baseSkillId*100+level</c> value such as 2031104) maps to its
    /// <c>SkillId</c> column (the base skill the SkillTable keys on, e.g. 20311).
    /// This is the game's own authoritative mapping — the same column
    /// <c>GameDataResonance.ResolveBaseSkillId</c> reads. Damage events carry the
    /// leveled id, so <see cref="GameDataCombatService.GetSkill"/> uses this map to
    /// resolve a leveled id to its base skill's name on a direct-lookup miss.
    /// On any failure logs once and returns an empty map — never throws.
    /// </summary>
    private IReadOnlyDictionary<int, int> LoadSkillLevelToBase()
    {
        var rows = LoadEagerTable<BaseSkillRef>(
            label: "SkillFightLevel",
            typeName: "Bokura.SkillFightLevelTableBase",
            capacityHint: 4096,
            projector: (row, rowType) =>
            {
                var leveledId = ReadInt(row, rowType, "Id");
                if (leveledId == 0) return (0, default);
                var baseId = ReadInt(row, rowType, "SkillId");
                if (baseId == 0) return (0, default);
                return (leveledId, new BaseSkillRef(baseId));
            });

        var map = new Dictionary<int, int>(capacity: rows.Count);
        foreach (var kvp in rows)
        {
            map[kvp.Key] = kvp.Value.BaseSkillId;
        }
        return map;
    }

    // ===== Buff ===========================================================

    /// <summary>
    /// Build the Buff lookup from <c>Bokura.BuffTableBase</c>. Recon (Phase 5
    /// polish) re-confirmed the row schema: <c>Id, Name, Desc (NOT Description),
    /// Icon (NOT IconPath), BuffType (Int32)</c>. There is NO <c>IsNegative</c>
    /// / <c>IsDebuff</c> column on this build — <see cref="BuffInfo.IsDebuff"/>
    /// is hard-coded to false. Recon found no obvious debuff-flag column;
    /// negative-effect classification likely lives in a categorized flag-group
    /// elsewhere (<c>Tags</c> Int32Array is a candidate but unverified).
    /// </summary>
    private bool _firstBuffLogged;

    private IReadOnlyDictionary<int, BuffInfo> LoadBuffs()
        => LoadEagerTable<BuffInfo>(
            label: "Buff",
            typeName: "Bokura.BuffTableBase",
            capacityHint: 1024,
            projector: ProjectBuffRow);

    // Project one BuffTableBase row into (id, BuffInfo). Shared by the eager whole-table load and the
    // single-row live fetch (LoadBuffLive) so both produce identical BuffInfo (Name/Desc/Icon/Category).
    private (int id, BuffInfo info) ProjectBuffRow(object row, Type rowType)
    {
        var id = ReadInt(row, rowType, "Id");
        if (id == 0) return (0, default);

        var (name, nameDesign) = ReadBuffName(row, rowType, id);
        // BuffTableBase column is `Desc` (no `Description`).
        var desc = ReadStringOrMlString(row, rowType, "Desc");
        if (string.IsNullOrEmpty(desc))
        {
            desc = ReadStringOrMlString(row, rowType, "Description");
        }
        // BuffTableBase column is `Icon` (not `IconPath`).
        var iconPath = ReadString(row, rowType, "Icon");
        if (string.IsNullOrEmpty(iconPath))
        {
            iconPath = ReadString(row, rowType, "IconPath");
        }
        var buffTypeInt = ReadInt(row, rowType, "BuffType");
        // BPSR-ZDPS reference: EBuffType { Debuff=0, Gain=1, GainRecovery=2, Item=3 }.
        // The old `IsNegative` column does not exist on this build (it read false for
        // every row), so derive the debuff flag from BuffType instead.
        var isDebuff = buffTypeInt == 0;
        // BuffTable.SkillId — the skill that applies this buff. Lets the CooldownBar
        // attribute an Imagine-lockout debuff to its source Imagine. 0 when absent.
        var skillId = ReadInt(row, rowType, "SkillId");

        if (!_firstBuffLogged)
        {
            _firstBuffLogged = true;
            LogFirstBuffRow(new BuffRowDiagInfo(id, name, nameDesign, desc, iconPath, buffTypeInt, isDebuff));
        }

        return (id, new BuffInfo(
            Id: id,
            Name: name ?? string.Empty,
            Description: desc ?? string.Empty,
            IconPath: iconPath ?? string.Empty,
            Category: MapBuffCategory(buffTypeInt),
            IsDebuff: isDebuff,
            SkillId: skillId));
    }

    /// <summary>
    /// Resolve a buff row's display name plus its raw <c>NameDesign</c> (returned
    /// for the diagnostics dump). The localized <c>Name</c> is primary; when it is
    /// empty the locale-gated <see cref="ResolveEmptyName"/> decides between the
    /// Chinese design label (Chinese client), a curated English override, or an
    /// id-based label (non-Chinese client) — never raw Chinese on an English client.
    /// Many internal/proc buffs (e.g. lance lucky-counter "法杖-幸运") have an empty
    /// localized <c>Name</c> in this client. Flows to CombatMeter / CooldownBar via
    /// <c>GetBuff().Name</c>.
    /// </summary>
    private (string name, string nameDesign) ReadBuffName(object row, Type rowType, int id)
    {
        var name = ReadStringOrMlString(row, rowType, "Name");
        var nameDesign = ReadStringOrMlString(row, rowType, "NameDesign");
        if (string.IsNullOrEmpty(name))
        {
            name = ResolveEmptyName("Buff", id, nameDesign);
        }
        return (name, nameDesign);
    }
}
