using System.Collections.Generic;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound port: reads the three game talent tables the spec-root-buff map is derived from
/// (spec-from-talent-buffs, 2026-09-26). Implemented in Infrastructure against the live
/// <c>Bokura.TalentStageTableBase</c> / <c>TalentTreeTableBase</c> / <c>TalentTableBase</c>; the derivation itself
/// is pure Application logic (<see cref="Services.SpecRootBuffs.Derive"/>). Called once, on the game thread,
/// after the deferred game-data drain completes.
/// </summary>
internal interface ISpecRootBuffSource
{
    /// <summary>False when any table is unavailable (type missing, reflection failed, zero rows).</summary>
    bool TryReadTalentTables(out SpecTalentTables tables);
}

/// <summary>One <c>TalentStageTable</c> row: <paramref name="WeaponType"/> = profession id,
/// <paramref name="BdType"/> 0/1 = first/second spec, <paramref name="TalentStage"/> 1 = Expertise II,
/// <paramref name="RootId"/> = a <c>TalentTreeTable</c> id (0 for transform "classes").</summary>
internal readonly record struct TalentStageRow(int WeaponType, int BdType, int TalentStage, int RootId);

/// <summary>One <c>TalentTable</c> row's <c>TalentEffect</c> list; entries are <c>[type, id, level]</c>
/// where type 3 = grant buff.</summary>
internal readonly record struct TalentEffectRow(int[][] Effects);

/// <summary>The talent tables keyed by row id: stages, tree node → <c>TalentId</c>, talent → effects.</summary>
internal sealed record SpecTalentTables(
    IReadOnlyDictionary<int, TalentStageRow> Stages,
    IReadOnlyDictionary<int, int> TreeTalentIds,
    IReadOnlyDictionary<int, TalentEffectRow> Talents);
