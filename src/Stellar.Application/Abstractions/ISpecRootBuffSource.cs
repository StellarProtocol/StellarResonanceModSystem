using System.Collections.Generic;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound port: reads the three game talent tables the spec-root-buff map is derived from
/// (spec-from-talent-buffs, 2026-09-26). Implemented in Infrastructure against the live
/// <c>Bokura.TalentStageTableBase</c> / <c>TalentTreeTableBase</c> / <c>TalentTableBase</c>; the derivation itself
/// is pure Application logic (<see cref="Services.SpecRootBuffs.Derive"/>). <see cref="Services.SpecRootBuffMap"/>
/// calls ONE member per game-thread tick (stages → trees → effects) once the deferred game-data drain is done,
/// and narrows each later read to the ids the previous table named, so only the 18 roots are projected.
/// Every member returns an empty dictionary when its table is unavailable.
/// </summary>
internal interface ISpecRootBuffSource
{
    /// <summary>All <c>TalentStageTable</c> rows, keyed by row id.</summary>
    IReadOnlyDictionary<int, TalentStageRow> ReadTalentStages();

    /// <summary><c>TalentTreeTable</c> id → <c>TalentId</c>, for the listed tree ids only.</summary>
    IReadOnlyDictionary<int, int> ReadTalentTreeTalentIds(IReadOnlyCollection<int> treeIds);

    /// <summary><c>TalentTable</c> id → <c>TalentEffect</c> rows, for the listed talent ids only.</summary>
    IReadOnlyDictionary<int, TalentEffectRow> ReadTalentEffects(IReadOnlyCollection<int> talentIds);
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
