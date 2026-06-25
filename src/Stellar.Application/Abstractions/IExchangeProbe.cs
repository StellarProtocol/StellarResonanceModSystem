using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Exchange;

namespace Stellar.Application.Abstractions;

/// <summary>Outbound boundary for the game's exchange system (driven via the WorldProxy exchange RPCs).
/// Implemented in Infrastructure.</summary>
internal interface IExchangeProbe
{
    /// <summary>True once the game-side Lua bridge is resolved.</summary>
    bool IsResolved { get; }

    /// <summary>Query live listings for an item.</summary>
    Task<IReadOnlyList<ExchangeListing>> QueryListingsAsync(int itemId, CancellationToken ct);

    /// <summary>Query the care/watch list of the given kind.</summary>
    Task<IReadOnlyList<ExchangeCareItem>> QueryCareListAsync(ExchangeItemKind kind, CancellationToken ct);

    /// <summary>Query one Trading-Center catalog category page (items + availability + min price). The family
    /// is derived from <paramref name="category"/>'s leading digit (1=Growth/2=Life Skills/3=Modules/4=Appearance).</summary>
    Task<IReadOnlyList<ExchangeCatalogItem>> QueryCatalogAsync(int category, CancellationToken ct);

    /// <summary>Query scheduled notice listings for an item.</summary>
    Task<IReadOnlyList<ExchangeNoticeListing>> QueryNoticeAsync(int itemId, CancellationToken ct);

    /// <summary>Dispatch the native buy (one <c>WorldProxy.ExchangeBuyItem</c> RPC, fired exactly once).
    /// Returns the raw result for the service to map. <b>A <see cref="ExchangeBuyRaw.TimedOut"/> result is
    /// INDETERMINATE</b> — the server may have completed the purchase even though no reply was observed in
    /// time. Callers MUST NOT blind-retry a timed-out buy; reconcile against the inventory/currency delta
    /// first (a retry can double-buy).</summary>
    Task<ExchangeBuyRaw> BuyAsync(int itemId, int quantity, long price, CancellationToken ct);
}

/// <summary>Raw buy result from the game's <c>ExchangeBuyItem</c> RPC: <c>Ok</c> is <c>ServerCode == 0</c>,
/// <c>ServerCode</c> is the returned <c>EErrorCode</c> (null if the reply was unparseable), <c>TimedOut</c>
/// means no reply arrived in time — INDETERMINATE (the buy may still have succeeded server-side).</summary>
internal readonly record struct ExchangeBuyRaw(bool Ok, int? ServerCode, bool TimedOut);
