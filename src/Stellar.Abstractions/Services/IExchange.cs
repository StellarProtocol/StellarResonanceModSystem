using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Exchange;

namespace Stellar.Abstractions.Services;

/// <summary>Query the in-game player exchange/marketplace and buy items through the game's own
/// trade system. Buying drives the game's native buy (server-validated); it never builds packets
/// or bypasses game-side checks. All reads are point-in-time async requests.</summary>
public interface IExchange
{
    /// <summary>True once the game-side trade API has been resolved and is callable.</summary>
    bool IsAvailable { get; }

    /// <summary>Fetch the current live listings for an item, cheapest-first when the game orders them.</summary>
    /// <param name="itemId">The item's config id.</param>
    /// <param name="ct">Cancels the request before dispatch.</param>
    /// <returns>The listings; empty when unavailable or none exist.</returns>
    Task<IReadOnlyList<ExchangeListing>> QueryListingsAsync(int itemId, CancellationToken ct = default);

    /// <summary>Fetch the watch ("care") list of the given kind, with per-item availability.</summary>
    /// <param name="kind">Which item kind to query.</param>
    /// <param name="ct">Cancels the request before dispatch.</param>
    /// <returns>The care-list items; empty when unavailable or none exist.</returns>
    Task<IReadOnlyList<ExchangeCareItem>> QueryCareListAsync(ExchangeItemKind kind, CancellationToken ct = default);

    /// <summary>Fetch one Trading-Center catalog category page: the items in <paramref name="category"/>,
    /// each with current availability and cheapest price. This is the catalog browse (by category), distinct
    /// from the per-item <see cref="QueryListingsAsync"/>. To assemble a full catalog, query each category and
    /// union the results — there is no single "all items" page.</summary>
    /// <param name="category">The Trading-Center category leaf id. Its leading digit is the family
    /// (1=Growth Items: 101 Ability/102 Gear/103 Will/104 Imagine; 2=Life Skills: 201–209; 3=Modules: 301;
    /// 4=Appearance: 401–405). The implementation derives the family from this id.</param>
    /// <param name="ct">Cancels the request before dispatch.</param>
    /// <returns>The category's items; empty when unavailable or none exist.</returns>
    Task<IReadOnlyList<ExchangeCatalogItem>> QueryCatalogAsync(int category, CancellationToken ct = default);

    /// <summary>Fetch scheduled ("notice"/pre-order) listings for an item, carrying their listing times.</summary>
    /// <param name="itemId">The item's config id.</param>
    /// <param name="ct">Cancels the request before dispatch.</param>
    /// <returns>The notice listings; empty when unavailable or none exist.</returns>
    Task<IReadOnlyList<ExchangeNoticeListing>> QueryNoticeAsync(int itemId, CancellationToken ct = default);

    /// <summary>Buy <paramref name="quantity"/> of an item at <paramref name="price"/> via the game's
    /// own trade buy. The game builds the request and validates it server-side.</summary>
    /// <param name="itemId">The item's config id.</param>
    /// <param name="quantity">How many to buy.</param>
    /// <param name="price">The unit price to pay (match a current listing).</param>
    /// <param name="ct">Cancels the request before dispatch.</param>
    /// <returns>The game's outcome for the buy.</returns>
    Task<ExchangeBuyOutcome> BuyAsync(int itemId, int quantity, long price, CancellationToken ct = default);
}
