using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Application.Services;

/// <summary>
/// Generated EAttrType catalog: id → enum name, English display name, value format
/// (<see cref="AttributeInfo.NumType"/>), and screen group. Backfills
/// <see cref="GameDataCombatService.GetAttribute"/> where the live game table is sparse.
/// Data half lives in <c>AttrCatalog.g.cs</c> — regenerate with
/// <c>tools/gen-attr-catalog.py</c> after a game patch; never edit it by hand.
/// </summary>
internal static partial class AttrCatalog
{
    private static readonly Dictionary<int, AttributeInfo> ById;

    static AttrCatalog()
    {
        ById = Build();
    }

    private static Dictionary<int, AttributeInfo> Build()
    {
        var map = new Dictionary<int, AttributeInfo>(Entries.Length);
        foreach (var e in Entries)
        {
            map[e.Id] = new AttributeInfo(e.Id, e.Name, ShortName: e.Name, IconPath: "", Group: e.Group)
            { NumType = e.NumType, EnumName = e.EnumName };
        }
        return map;
    }

    /// <summary>Catalog row for an EAttrType id, or null when the id isn't catalogued.</summary>
    public static AttributeInfo? TryGet(int id)
        => ById.TryGetValue(id, out var info) ? info : (AttributeInfo?)null;
}
