using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Exchange;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>Reflection-based <see cref="IExchangeProbe"/>. Drives the game's <c>trade</c> Lua VM
/// (colon-style: <c>vm:AsyncExchangeBuyItem(...)</c> etc.) through the tolua# bridge rather than
/// building packets (mirror of <see cref="PandaLoadoutProbe"/>). Dispatch is queued and drained on
/// the Update tick (the Lua VM is main-thread-only).</summary>
internal sealed partial class PandaExchangeProbe : IExchangeProbe
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(8);

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;
    private readonly ConcurrentQueue<Action> _toDispatch = new();

    public PandaExchangeProbe(IPluginLog log, IGameTypeRegistry typeRegistry)
    {
        _log = log;
        _typeRegistry = typeRegistry;
    }

    public bool IsResolved => _bridgeResolved;

    // Drained from the Host service tick (main thread).
    internal void DrainPendingDispatches()
    {
        TryResolveBridgeIfDue();
        if (!_bridgeResolved) return;
        while (_toDispatch.TryDequeue(out var act))
        {
            try { act(); } catch (Exception ex) { _log.Warning($"[Stellar][Exchange] dispatch threw: {ex.Message}"); }
        }

        CompletePendingResults();
    }

    public Task<ExchangeBuyRaw> BuyAsync(int itemId, int quantity, long price, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ExchangeBuyRaw>();
        _toDispatch.Enqueue(() =>
        {
            rawsetClear();
            InvokeChunk(BuildBuyChunk(itemId, quantity, price));
            // The chunk writes ResultGlobal a frame+ later; poll it on subsequent ticks (Step 6 refines).
            _pendingBuy = new PendingBuy(tcs, DateTime.UtcNow);
        });
        return tcs.Task;
    }

    public Task<IReadOnlyList<ExchangeCareItem>> QueryCareListAsync(ExchangeItemKind kind, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<ExchangeCareItem>>();
        _toDispatch.Enqueue(() =>
        {
            rawsetClear();
            InvokeChunk(BuildCareListChunk(kind == ExchangeItemKind.NoticeShopItem ? 2 : 1));
            _pendingCare = new PendingCare(tcs, DateTime.UtcNow);
        });
        return tcs.Task;
    }

    // Listings/notice: model-read path located in Step 6. Until then return empty so callers degrade gracefully.
    public Task<IReadOnlyList<ExchangeListing>> QueryListingsAsync(int itemId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ExchangeListing>>(Array.Empty<ExchangeListing>());

    public Task<IReadOnlyList<ExchangeNoticeListing>> QueryNoticeAsync(int itemId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ExchangeNoticeListing>>(Array.Empty<ExchangeNoticeListing>());

    private void rawsetClear() => InvokeChunk("rawset(_G,\"" + ResultGlobal + "\", nil)");

    // INTERIM (pre-Step-6): the full result-poll — read ResultGlobal, parse "BUY:true" /
    // the "CARE\n…" lines — is added in Step 6 alongside the live discovery. Until then,
    // complete any pending request that has outlived CompletionTimeout so BuyAsync /
    // QueryCareListAsync never hang and the _pendingBuy/_pendingCare fields are used.
    private void CompletePendingResults()
    {
        var now = DateTime.UtcNow;
        if (_pendingBuy is { } buy && now - buy.Started >= CompletionTimeout)
        {
            _pendingBuy = null;
            buy.Tcs.TrySetResult(new ExchangeBuyRaw(false, null, true));
        }
        if (_pendingCare is { } care && now - care.Started >= CompletionTimeout)
        {
            _pendingCare = null;
            care.Tcs.TrySetResult(Array.Empty<ExchangeCareItem>());
        }
    }

    private PendingBuy? _pendingBuy;
    private PendingCare? _pendingCare;
    private sealed record PendingBuy(TaskCompletionSource<ExchangeBuyRaw> Tcs, DateTime Started);
    private sealed record PendingCare(TaskCompletionSource<IReadOnlyList<ExchangeCareItem>> Tcs, DateTime Started);
}
