using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Exchange;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Wraps <see cref="IExchangeProbe"/> to expose <see cref="IExchange"/>. Pure pass-through
/// for queries; maps the raw buy result to <see cref="ExchangeBuyOutcome"/>. No cached state — the
/// exchange is request/response, so there is no Tick/changed-event (unlike loadout).</summary>
internal sealed class ExchangeService : IExchange
{
    private static readonly IReadOnlyList<ExchangeListing> NoListings = Array.Empty<ExchangeListing>();
    private static readonly IReadOnlyList<ExchangeCareItem> NoCare = Array.Empty<ExchangeCareItem>();
    private static readonly IReadOnlyList<ExchangeNoticeListing> NoNotice = Array.Empty<ExchangeNoticeListing>();
    private static readonly IReadOnlyList<ExchangeCatalogItem> NoCatalog = Array.Empty<ExchangeCatalogItem>();

    private readonly IExchangeProbe _probe;

    public ExchangeService(IExchangeProbe probe) => _probe = probe;

    public bool IsAvailable => _probe.IsResolved;

    public Task<IReadOnlyList<ExchangeListing>> QueryListingsAsync(int itemId, CancellationToken ct = default)
        => _probe.IsResolved ? _probe.QueryListingsAsync(itemId, ct) : Task.FromResult(NoListings);

    public Task<IReadOnlyList<ExchangeCareItem>> QueryCareListAsync(ExchangeItemKind kind, CancellationToken ct = default)
        => _probe.IsResolved ? _probe.QueryCareListAsync(kind, ct) : Task.FromResult(NoCare);

    public Task<IReadOnlyList<ExchangeNoticeListing>> QueryNoticeAsync(int itemId, CancellationToken ct = default)
        => _probe.IsResolved ? _probe.QueryNoticeAsync(itemId, ct) : Task.FromResult(NoNotice);

    public Task<IReadOnlyList<ExchangeCatalogItem>> QueryCatalogAsync(ExchangeItemKind kind, int category, CancellationToken ct = default)
        => _probe.IsResolved ? _probe.QueryCatalogAsync(kind, category, ct) : Task.FromResult(NoCatalog);

    public async Task<ExchangeBuyOutcome> BuyAsync(int itemId, int quantity, long price, CancellationToken ct = default)
    {
        if (!_probe.IsResolved) return ExchangeBuyOutcome.Timeout;
        return MapOutcome(await _probe.BuyAsync(itemId, quantity, price, ct).ConfigureAwait(false));
    }

    private static ExchangeBuyOutcome MapOutcome(ExchangeBuyRaw raw)
    {
        if (raw.TimedOut) return ExchangeBuyOutcome.Timeout;
        if (raw.Ok) return ExchangeBuyOutcome.Success;
        return raw.ServerCode switch
        {
            6457 => ExchangeBuyOutcome.NoItemAvailable,
            6467 => ExchangeBuyOutcome.InsufficientFunds,
            _ => ExchangeBuyOutcome.Rejected,
        };
    }
}
