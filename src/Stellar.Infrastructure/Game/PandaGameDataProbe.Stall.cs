using System.Collections.Generic;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

// Live Trading-Center membership: read Bokura.StallDetailTableBase once (after HybridCLR
// loads it), cache the ItemId->Subcategory map. Reuses the shared eager-table loader.
internal sealed partial class PandaGameDataProbe : IStallSubcategorySource
{
    private IReadOnlyDictionary<int, int>? _stallSubcategories;

    public IReadOnlyDictionary<int, int> GetStallSubcategories()
        => _stallSubcategories ??= LoadStallSubcategories();

    private IReadOnlyDictionary<int, int> LoadStallSubcategories()
    {
        var rows = LoadEagerTable<StallRow>(
            label: "StallDetail",
            typeName: "Bokura.StallDetailTableBase",
            capacityHint: 1024,
            projector: (row, rowType) =>
            {
                // Row key is the item config id. Recon: column is "Id"; fall back to
                // "ItemId" if a future patch renames it (first-row diag confirms live).
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) id = ReadInt(row, rowType, "ItemId");
                var sub = ReadInt(row, rowType, "Subcategory");
                return (id, new StallRow(sub));
            });

        var map = new Dictionary<int, int>(rows.Count);
        foreach (var kv in rows) map[kv.Key] = kv.Value.Subcategory;
        return map;
    }

    private readonly record struct StallRow(int Subcategory);   // LoadEagerTable needs a struct TInfo
}
