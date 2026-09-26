using System.Collections.Generic;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="ISpecRootBuffSource"/> over the live Bokura talent tables (contract and call pattern on the
/// interface). Reads go through the shared deferred-table envelope (one <c>[Stellar][GameData] deferred: …
/// loaded</c> line each, empty dictionary on failure); the tree and talent reads skip every row outside the
/// requested ids before any column is read.
/// </summary>
internal sealed partial class PandaGameDataProbe : ISpecRootBuffSource
{
    public IReadOnlyDictionary<int, TalentStageRow> ReadTalentStages() =>
        LoadDeferredTable<TalentStageRow>(
            label: "TalentStage",
            typeName: "Bokura.TalentStageTableBase",
            capacityHint: 64,
            projector: (row, rowType) => (ReadInt(row, rowType, "Id"), new TalentStageRow(
                WeaponType: ReadInt(row, rowType, "WeaponType"),
                BdType: ReadInt(row, rowType, "BdType"),
                TalentStage: ReadInt(row, rowType, "TalentStage"),
                RootId: ReadInt(row, rowType, "RootId"))));

    public IReadOnlyDictionary<int, int> ReadTalentTreeTalentIds(IReadOnlyCollection<int> treeIds)
    {
        var wanted = AsSet(treeIds);
        return LoadDeferredTable<int>(
            label: "TalentTreeRoots",
            typeName: "Bokura.TalentTreeTableBase",
            capacityHint: wanted.Count,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                return wanted.Contains(id) ? (id, ReadInt(row, rowType, "TalentId")) : (0, 0);
            });
    }

    public IReadOnlyDictionary<int, TalentEffectRow> ReadTalentEffects(IReadOnlyCollection<int> talentIds)
    {
        var wanted = AsSet(talentIds);
        return LoadDeferredTable<TalentEffectRow>(
            label: "TalentRootEffects",
            typeName: "Bokura.TalentTableBase",
            capacityHint: wanted.Count,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                return wanted.Contains(id)
                    ? (id, new TalentEffectRow(ReadInt32Array2D(row, rowType, "TalentEffect")))
                    : (0, default);
            });
    }

    private static ISet<int> AsSet(IReadOnlyCollection<int> ids) => ids as ISet<int> ?? new HashSet<int>(ids);
}
