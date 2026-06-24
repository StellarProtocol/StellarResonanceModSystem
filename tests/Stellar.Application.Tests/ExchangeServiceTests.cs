using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Exchange;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests;

public sealed class ExchangeServiceTests
{
    private sealed class FakeProbe : IExchangeProbe
    {
        public bool Resolved = true;
        public IReadOnlyList<ExchangeListing> Listings = new List<ExchangeListing>();
        public ExchangeBuyRaw BuyReturns = new(true, null, false);
        public int BoughtItem = -1, BoughtQty = -1; public long BoughtPrice = -1;
        public ExchangeItemKind CareKind;

        public bool IsResolved => Resolved;
        public Task<IReadOnlyList<ExchangeListing>> QueryListingsAsync(int itemId, CancellationToken ct) => Task.FromResult(Listings);
        public Task<IReadOnlyList<ExchangeCareItem>> QueryCareListAsync(ExchangeItemKind kind, CancellationToken ct)
        { CareKind = kind; return Task.FromResult<IReadOnlyList<ExchangeCareItem>>(new List<ExchangeCareItem>()); }
        public Task<IReadOnlyList<ExchangeNoticeListing>> QueryNoticeAsync(int itemId, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExchangeNoticeListing>>(new List<ExchangeNoticeListing>());
        public Task<ExchangeBuyRaw> BuyAsync(int itemId, int quantity, long price, CancellationToken ct)
        { BoughtItem = itemId; BoughtQty = quantity; BoughtPrice = price; return Task.FromResult(BuyReturns); }
    }

    [Fact]
    public void IsAvailable_reflects_probe_resolution()
    {
        Assert.False(new ExchangeService(new FakeProbe { Resolved = false }).IsAvailable);
        Assert.True(new ExchangeService(new FakeProbe { Resolved = true }).IsAvailable);
    }

    [Fact]
    public async Task QueryListingsAsync_delegates_to_probe()
    {
        var probe = new FakeProbe { Listings = new List<ExchangeListing> { new(101, 50, "u") } };
        var svc = new ExchangeService(probe);
        var r = await svc.QueryListingsAsync(101);
        Assert.Single(r);
        Assert.Equal(50, r[0].Price);
    }

    [Fact]
    public async Task QueryCareListAsync_passes_kind()
    {
        var probe = new FakeProbe();
        await new ExchangeService(probe).QueryCareListAsync(ExchangeItemKind.NoticeShopItem);
        Assert.Equal(ExchangeItemKind.NoticeShopItem, probe.CareKind);
    }

    [Fact]
    public async Task BuyAsync_passes_args_and_maps_success()
    {
        var probe = new FakeProbe { BuyReturns = new(true, null, false) };
        var svc = new ExchangeService(probe);
        var outcome = await svc.BuyAsync(101, 2, 50);
        Assert.Equal(101, probe.BoughtItem);
        Assert.Equal(2, probe.BoughtQty);
        Assert.Equal(50, probe.BoughtPrice);
        Assert.Equal(ExchangeBuyOutcome.Success, outcome);
    }

    [Theory]
    [InlineData(true, null, false, ExchangeBuyOutcome.Success)]
    [InlineData(false, null, false, ExchangeBuyOutcome.Rejected)]
    [InlineData(false, 6457, false, ExchangeBuyOutcome.NoItemAvailable)]
    [InlineData(false, 6467, false, ExchangeBuyOutcome.InsufficientFunds)]
    [InlineData(false, null, true, ExchangeBuyOutcome.Timeout)]
    public async Task BuyAsync_maps_raw_result(bool ok, int? code, bool timedOut, ExchangeBuyOutcome expected)
    {
        var probe = new FakeProbe { BuyReturns = new(ok, code, timedOut) };
        Assert.Equal(expected, await new ExchangeService(probe).BuyAsync(1, 1, 1));
    }

    [Fact]
    public async Task QueryListingsAsync_returns_empty_when_unresolved()
    {
        var svc = new ExchangeService(new FakeProbe { Resolved = false });
        Assert.Empty(await svc.QueryListingsAsync(1));
    }
}
