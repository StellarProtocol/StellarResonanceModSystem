using System;

namespace Stellar.Abstractions.Domain.Exchange;

/// <summary>One live exchange listing for an item: the unit price and the listing's uuid.</summary>
/// <param name="ItemId">The item's config id.</param>
/// <param name="Price">Unit price in the exchange currency (Luno).</param>
/// <param name="Uuid">The listing's server uuid (empty if the game does not surface one).</param>
public readonly record struct ExchangeListing(int ItemId, long Price, string Uuid);

/// <summary>An item present on the exchange "care" (watch) list, with its current availability.</summary>
/// <param name="ItemId">The item's config id.</param>
/// <param name="Available">How many are currently available.</param>
public readonly record struct ExchangeCareItem(int ItemId, int Available);

/// <summary>A scheduled ("notice"/pre-order) listing not yet live: price and the time it lists.</summary>
/// <param name="ItemId">The item's config id.</param>
/// <param name="Price">Expected unit price.</param>
/// <param name="ListingTime">When the item is scheduled to become buyable.</param>
public readonly record struct ExchangeNoticeListing(int ItemId, long Price, DateTimeOffset ListingTime);

/// <summary>Which exchange item kind a care-list query targets.</summary>
public enum ExchangeItemKind
{
    /// <summary>Normal, currently-listed shop items.</summary>
    ShopItem,
    /// <summary>Scheduled "notice"/pre-order items not yet live.</summary>
    NoticeShopItem,
}

/// <summary>The game's outcome for a buy attempt, mapped from the trade VM's result.</summary>
public enum ExchangeBuyOutcome
{
    /// <summary>The buy succeeded.</summary>
    Success,
    /// <summary>No matching item was available to buy.</summary>
    NoItemAvailable,
    /// <summary>The player could not afford the purchase.</summary>
    InsufficientFunds,
    /// <summary>The game rejected the buy for another reason (it toasts the cause).</summary>
    Rejected,
    /// <summary>The request did not resolve in time. <b>Indeterminate for a buy</b> — the purchase may
    /// still have succeeded server-side; do not blind-retry, reconcile against inventory/currency first.</summary>
    Timeout,
}
