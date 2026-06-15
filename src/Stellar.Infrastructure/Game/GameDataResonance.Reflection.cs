using System;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Infrastructure.Game;

internal sealed partial class GameDataResonance
{
    // ===== Skill resolution ===============================================

    // Resolve a Battle Imagine from SkillTableBase[skillId]. Returns false (so the
    // caller memoises a negative) unless the row exists AND its SlotPositionId
    // contains 7 or 8 (the aoyi slots that mark a Battle Imagine).
    private bool TryResolveImagine(int skillId, out ImagineInfo info)
    {
        info = default;
        if (skillId == 0) return false;

        var skillRow = GetRow("Bokura.SkillTableBase", skillId);
        if (skillRow is null) return false;
        _skillTableReady = true;   // a row read back → the table is loaded; negatives are now safe to memoise

        var rowType = skillRow.GetType();
        if (!IsImagineSlot(skillRow, rowType)) return false;

        var name = ReadStringOrMl(skillRow, rowType, "Name");
        var icon = ReadString(skillRow, rowType, "Icon");
        var chargeCount = ReadInt(skillRow, rowType, "MaxEnergyChargeNum");
        var rechargeMs = (int)ReadLong(skillRow, rowType, "EnergyChargeTime");
        var cooldownMs = ResolveSingleCastCooldownMs(skillRow, rowType);

        info = new ImagineInfo(
            SkillId: skillId,
            Name: name,
            IconAddress: icon,
            ChargeCount: chargeCount <= 0 ? 1 : chargeCount,
            RechargeMs: rechargeMs < 0 ? 0 : rechargeMs,
            CooldownMs: cooldownMs);
        return true;
    }

    // A skill is a Battle Imagine iff its SlotPositionId (int array) contains 7 or 8.
    private static bool IsImagineSlot(object skillRow, Type skillRowType)
    {
        var slots = ReadIntArray(skillRow, skillRowType, "SlotPositionId");
        foreach (var s in slots)
        {
            if (s == 7 || s == 8) return true;
        }
        return false;
    }

    // Authoritative leveled-id -> base skill id map. Cast/cooldown rows carry a
    // skill_level_id (SkillFightLevelTable key, e.g. 395001); that row's SkillId
    // column is the base skill (3950) the loadout/SkillTable keys on. This is the
    // game's own mapping — robust where the baseId*100+level heuristic is not
    // (the heuristic has real table-wide mismatches; SkillLevelGroup is unrelated —
    // it groups summon/variant skills and is 0 for Battle Imagines). Returns 0 on miss.
    private int ResolveBaseSkillId(int skillLevelId)
    {
        var fightRow = GetRow("Bokura.SkillFightLevelTableBase", skillLevelId);
        if (fightRow is null) return 0;
        return ReadInt(fightRow, fightRow.GetType(), "SkillId");
    }

    // EffectIDs[1] -> SkillFightLevelTableBase[effectId].PVECoolTime (seconds, float) -> ms.
    private int ResolveSingleCastCooldownMs(object skillRow, Type skillRowType)
    {
        var effectId = FirstOf(ReadIntArray(skillRow, skillRowType, "EffectIDs"));
        if (effectId == 0) effectId = FirstOf(ReadIntArray(skillRow, skillRowType, "EffectIds"));
        if (effectId == 0) return 0;

        var fightRow = GetRow("Bokura.SkillFightLevelTableBase", effectId);
        if (fightRow is null) return 0;

        var seconds = ReadFloat(fightRow, fightRow.GetType(), "PVECoolTime");
        if (seconds <= 0f) seconds = ReadFloat(fightRow, fightRow.GetType(), "PveCoolTime");
        return seconds <= 0f ? 0 : (int)(seconds * 1000f);
    }

    private static int FirstOf(int[] values) => values.Length > 0 ? values[0] : 0;
}
