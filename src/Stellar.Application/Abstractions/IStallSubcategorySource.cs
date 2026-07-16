using System.Collections.Generic;

namespace Stellar.Application.Abstractions;

/// <summary>Outbound source of the Trading-Center membership map, read live from the
/// game's StallDetailTable. Implemented by the Infrastructure game-data probe (which owns
/// the table loader) and consumed by the exchange probe.</summary>
internal interface IStallSubcategorySource
{
    /// <summary>Item config id → subcategory leaf id (101-104 / 201-209 / 301 / 401-405).
    /// Empty until the game tables are loaded; empty (not null) on any failure.</summary>
    IReadOnlyDictionary<int, int> GetStallSubcategories();
}
