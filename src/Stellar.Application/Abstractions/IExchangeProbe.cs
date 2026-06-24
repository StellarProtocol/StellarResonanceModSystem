using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Exchange;

namespace Stellar.Application.Abstractions;

/// <summary>Outbound boundary for the game's trade/exchange system. Implemented in Infrastructure.</summary>
internal interface IExchangeProbe
{
    /// <summary>True once the game-side trade bridge is resolved.</summary>
    bool IsResolved { get; }

    /// <summary>Query live listings for an item.</summary>
    Task<IReadOnlyList<ExchangeListing>> QueryListingsAsync(int itemId, CancellationToken ct);

    /// <summary>Query the care/watch list of the given kind.</summary>
    Task<IReadOnlyList<ExchangeCareItem>> QueryCareListAsync(ExchangeItemKind kind, CancellationToken ct);

    /// <summary>Query scheduled notice listings for an item.</summary>
    Task<IReadOnlyList<ExchangeNoticeListing>> QueryNoticeAsync(int itemId, CancellationToken ct);

    /// <summary>Dispatch the native buy. Returns the raw result for the service to map.</summary>
    Task<ExchangeBuyRaw> BuyAsync(int itemId, int quantity, long price, CancellationToken ct);
}

/// <summary>Raw buy result from the trade VM: the wrapper's bool plus an optional server code
/// (null when only the bool is observable).</summary>
internal readonly record struct ExchangeBuyRaw(bool Ok, int? ServerCode, bool TimedOut);
