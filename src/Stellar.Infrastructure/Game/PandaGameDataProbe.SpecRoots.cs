using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="ISpecRootBuffSource"/> — reads the three talent tables the spec-root-buff map is derived from
/// (spec-from-talent-buffs, 2026-09-26): <c>Bokura.TalentStageTableBase</c> (WeaponType / BdType /
/// TalentStage / RootId), <c>Bokura.TalentTreeTableBase</c> (Id → TalentId) and <c>Bokura.TalentTableBase</c>
/// (Id → TalentEffect <c>[type, id, level]</c> rows). Uses the shared deferred-table envelope, so each table
/// logs its own <c>[Stellar][GameData] deferred: … loaded</c> line and a failure yields an empty dictionary
/// (never throws). Called once on the game thread after the deferred game-data drain completes; the
/// derivation is Application's (<c>SpecRootBuffs.Derive</c>).
/// </summary>
internal sealed partial class PandaGameDataProbe : ISpecRootBuffSource
{
    public bool TryReadTalentTables(out SpecTalentTables tables)
    {
        var stages = LoadDeferredTable<TalentStageRow>(
            label: "TalentStage",
            typeName: "Bokura.TalentStageTableBase",
            capacityHint: 64,
            projector: (row, rowType) => (ReadInt(row, rowType, "Id"), new TalentStageRow(
                WeaponType: ReadInt(row, rowType, "WeaponType"),
                BdType: ReadInt(row, rowType, "BdType"),
                TalentStage: ReadInt(row, rowType, "TalentStage"),
                RootId: ReadInt(row, rowType, "RootId"))));

        var trees = LoadDeferredTable<int>(
            label: "TalentTree",
            typeName: "Bokura.TalentTreeTableBase",
            capacityHint: 2048,
            projector: (row, rowType) => (ReadInt(row, rowType, "Id"), ReadInt(row, rowType, "TalentId")));

        var talents = LoadDeferredTable<TalentEffectRow>(
            label: "TalentEffect",
            typeName: "Bokura.TalentTableBase",
            capacityHint: 1024,
            projector: (row, rowType) => (ReadInt(row, rowType, "Id"),
                new TalentEffectRow(ReadInt32Array2D(row, rowType, "TalentEffect"))));

        tables = new SpecTalentTables(stages, trees, talents);
        return stages.Count > 0 && trees.Count > 0 && talents.Count > 0;
    }
}
