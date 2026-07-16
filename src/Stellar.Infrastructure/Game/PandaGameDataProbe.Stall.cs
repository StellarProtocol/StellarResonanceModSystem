using System;
using System.Collections.Generic;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

// Live Trading-Center membership: read Bokura.StallDetailTableBase once (after HybridCLR loads it),
// cache the ItemId->Subcategory map.
//
// StallDetailTableBase is a ZTable<System.Int32, StallDetailTableBase>: the item id is the DICTIONARY
// KEY, and the row VALUE exposes only Category + Subcategory (no id field). The shared value-only
// LoadEagerTable path discards the key (it reads KeyValuePair.Value), so it produced 0 usable rows.
// We enumerate KEY+VALUE pairs here and take the id from the key.
internal sealed partial class PandaGameDataProbe : IStallSubcategorySource
{
    private IReadOnlyDictionary<int, int>? _stallSubcategories;

    public IReadOnlyDictionary<int, int> GetStallSubcategories()
    {
        if (_stallSubcategories is { Count: > 0 }) return _stallSubcategories;
        return _stallSubcategories = LoadStallSubcategories();
    }

    private IReadOnlyDictionary<int, int> LoadStallSubcategories()
    {
        var map = new Dictionary<int, int>(capacity: 1024);
        try
        {
            var rowType = _typeRegistry.FindType("Bokura.StallDetailTableBase");
            if (rowType is null)
            {
                _log.Warning("[Stellar][GameData] missing type Bokura.StallDetailTableBase");
                return map;
            }
            if (!TryGetTable(rowType, out var table) || table is null) return map;

            var pairs = CollectKeyedRowsViaTypedEnumerator(table);
            foreach (var (key, value) in pairs)
            {
                if (value is null) continue;
                var itemId = CoerceToInt(key);
                if (itemId == 0) continue;
                var sub = ReadInt(value, value.GetType(), "Subcategory");
                if (sub != 0) map[itemId] = sub;
            }
            _log.Info($"[Stellar][GameData] eager: StallDetail loaded ({map.Count} rows keyed, from {pairs.Count} table entries)");
            LogStallMembership(map);   // full [id]=sub dump — gated on STELLAR_DIAGNOSTICS (see .Diagnostics.cs)
        }
        catch (Exception ex)
        {
            _log.Error($"[Stellar][GameData] LoadStallSubcategories threw: {ex.GetType().Name}: {ex.Message}");
        }
        return map;
    }

    // Coerce a boxed ZTable key (int internally, may surface as long) to int. Mirrors ReadInt's scalar
    // handling; kept local so the shared ReadInt is untouched.
    private static int CoerceToInt(object? v) => v switch
    {
        int i => i,
        long l => unchecked((int)l),
        uint u => unchecked((int)u),
        short s => s,
        _ => 0,
    };
}
