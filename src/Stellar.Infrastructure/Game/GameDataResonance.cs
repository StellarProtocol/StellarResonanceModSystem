using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-based <see cref="IGameDataResonance"/>. A skill is a Battle Imagine
/// (the "aoyi" skills) iff <c>Bokura.SkillTableBase[skillId].SlotPositionId</c>
/// contains 7 or 8. Each player's equipped loadout (from
/// <c>ICombatLookup.GetSkillLevels</c>) carries exactly two such skills.
///
/// <para>
/// Display data comes from <c>Bokura.SkillTableBase[skillId]</c>
/// (<c>Name</c> / <c>Icon</c> / <c>MaxEnergyChargeNum</c> / <c>EnergyChargeTime</c>),
/// and the single-cast cooldown from
/// <c>Bokura.SkillFightLevelTableBase[EffectIDs[1]].PVECoolTime</c> (sec→ms).
/// </para>
///
/// <para>
/// <see cref="GetImagineForSkill"/> memoises every result (including negatives) keyed
/// by skill id, so the per-row / per-tick render path runs the SkillTable reflection
/// at most once per skill id. All reflection is resolved on the game thread; the memo
/// is then read on the same (render) thread.
/// </para>
/// </summary>
internal sealed partial class GameDataResonance : IGameDataResonance
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly object[] AutoLoadTrueArgs = { true };

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;
    private readonly PandaMLStringResolver _mlStrings;

    // Memo of every GetImagineForSkill result so the per-row, per-tick render path never re-runs the SkillTable
    // reflection for the same skill id. Read/written on the render (game) thread only. NEGATIVE results are memoised
    // only AFTER the SkillTable has loaded (see _skillTableReady): caching a null while the table is still loading
    // would permanently hide an imagine for the rest of the session (a cast immediately on relaunch hit this).
    private readonly Dictionary<int, ImagineInfo?> _imagineMemo = new();
    private bool _skillTableReady;   // set true the first time any SkillTableBase row reads back (TryResolveImagine)

    public GameDataResonance(IPluginLog log, IGameTypeRegistry typeRegistry, PandaMLStringResolver mlStrings)
    {
        _log = log;
        _typeRegistry = typeRegistry;
        _mlStrings = mlStrings;
    }

    /// <inheritdoc/>
    public ImagineInfo? GetImagineForSkill(int skillId)
    {
        if (skillId <= 0) return null;
        if (_imagineMemo.TryGetValue(skillId, out var memo)) return memo;   // hot path: dict hit, no reflection

        ImagineInfo? result = TryResolveImagine(skillId, out var info) ? info : null;
        // Cast/cooldown rows carry a leveled skill_level_id (e.g. 395001) that is NOT in SkillTable.
        // Map it to its base skill via SkillFightLevelTable[id].SkillId (the game's own column) so observed
        // casts + LocalCooldowns resolve to the equipped imagine (whose ImagineInfo.SkillId stays the base —
        // that's what the loadout/cooldowns key on). The baseId*100+level heuristic is the last-ditch fallback.
        if (result is null && skillId > 99_999)
        {
            int baseId = ResolveBaseSkillId(skillId);
            if (baseId > 0 && baseId != skillId) result = GetImagineForSkill(baseId);
            if (result is null) result = GetImagineForSkill(skillId / 100);
        }
        // Cache positives always; cache negatives only once the table is loaded, so a pre-load null isn't stuck.
        if (result is not null || _skillTableReady) _imagineMemo[skillId] = result;
        return result;
    }
}
