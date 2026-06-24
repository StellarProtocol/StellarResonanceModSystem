using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Exchange;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>Reflection-based <see cref="IExchangeProbe"/> driving the game's <b>WorldProxy</b> exchange
/// RPCs directly (<c>zproxy.world_proxy</c>) through the tolua# bridge — "Approach A" in
/// <c>docs/driving-game-actions.md</c>, validated headless (the trade page never needs to be open;
/// <c>recon/exchange-vm-notes.md</c> Pass 5). We never build packets: the game's proxy serializes/sends and
/// the server validates. Each RPC runs inside <c>Z.CoroUtil.create_coro_xpcall</c>, resumes off the global
/// RPC pump, and writes its reply into a Lua global that C# reads back on the Update tick.
///
/// <para><b>Readiness retry:</b> the exchange service has a brief cold-start window after entering world — an
/// RPC fired too early hangs forever (single-shot). READ requests are therefore re-fired on an interval until
/// they resolve or time out. The <b>buy is fired exactly once</b> (never re-fired — each fire is a real
/// purchase); callers should prime with a read first so the service is ready by buy time.</para></summary>
internal sealed partial class PandaExchangeProbe : IExchangeProbe
{
    // Give up on a request after this long (covers the cold-start retry window).
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    // Re-fire interval for READ requests to clear the service cold-start hang.
    private static readonly TimeSpan RefireInterval = TimeSpan.FromSeconds(1.5);

    private static readonly IReadOnlyList<ExchangeCareItem> NoCare = Array.Empty<ExchangeCareItem>();
    private static readonly IReadOnlyList<ExchangeListing> NoListings = Array.Empty<ExchangeListing>();
    private static readonly IReadOnlyList<ExchangeNoticeListing> NoNotice = Array.Empty<ExchangeNoticeListing>();
    private static readonly IReadOnlyList<ExchangeCatalogItem> NoCatalog = Array.Empty<ExchangeCatalogItem>();

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    // Requests are registered off any thread and serviced on the main (tick) thread — the Lua VM is
    // main-thread-only. _active holds in-flight requests; one per kind (a new request supersedes the old).
    private readonly ConcurrentQueue<PendingRpc> _toRegister = new();
    private readonly List<PendingRpc> _active = new();

    public PandaExchangeProbe(IPluginLog log, IGameTypeRegistry typeRegistry)
    {
        _log = log;
        _typeRegistry = typeRegistry;
    }

    public bool IsResolved => _bridgeResolved;

    public Task<IReadOnlyList<ExchangeCareItem>> QueryCareListAsync(ExchangeItemKind kind, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<IReadOnlyList<ExchangeCareItem>>(ct);
        var tcs = new TaskCompletionSource<IReadOnlyList<ExchangeCareItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var typeArg = kind == ExchangeItemKind.NoticeShopItem ? 2 : 1;
        Enqueue(new PendingRpc(KindCare, CareGlobal, BuildCareChunk(typeArg), allowRefire: true)
        {
            OnResult = s => tcs.TrySetResult(ParseCare(s)),
            OnTimeout = () => tcs.TrySetResult(NoCare),
        });
        return tcs.Task;
    }

    public Task<IReadOnlyList<ExchangeListing>> QueryListingsAsync(int itemId, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<IReadOnlyList<ExchangeListing>>(ct);
        var tcs = new TaskCompletionSource<IReadOnlyList<ExchangeListing>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new PendingRpc(KindListings, ListingsGlobal, BuildListingsChunk(itemId), allowRefire: true)
        {
            OnResult = s => tcs.TrySetResult(ParseListings(s, itemId)),
            OnTimeout = () => tcs.TrySetResult(NoListings),
        });
        return tcs.Task;
    }

    public Task<IReadOnlyList<ExchangeCatalogItem>> QueryCatalogAsync(ExchangeItemKind kind, int category, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<IReadOnlyList<ExchangeCatalogItem>>(ct);
        var tcs = new TaskCompletionSource<IReadOnlyList<ExchangeCatalogItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var typeArg = kind == ExchangeItemKind.NoticeShopItem ? 2 : 1;
        Enqueue(new PendingRpc(KindCatalog, CatalogGlobal, BuildCatalogChunk(typeArg, category), allowRefire: true)
        {
            OnResult = s => tcs.TrySetResult(ParseCatalog(s)),
            OnTimeout = () => tcs.TrySetResult(NoCatalog),
        });
        return tcs.Task;
    }

    public Task<IReadOnlyList<ExchangeNoticeListing>> QueryNoticeAsync(int itemId, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<IReadOnlyList<ExchangeNoticeListing>>(ct);
        var tcs = new TaskCompletionSource<IReadOnlyList<ExchangeNoticeListing>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new PendingRpc(KindNotice, NoticeGlobal, BuildNoticeChunk(itemId), allowRefire: true)
        {
            OnResult = s => tcs.TrySetResult(ParseNotice(s, itemId)),
            OnTimeout = () => tcs.TrySetResult(NoNotice),
        });
        return tcs.Task;
    }

    public Task<ExchangeBuyRaw> BuyAsync(int itemId, int quantity, long price, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<ExchangeBuyRaw>(ct);
        var tcs = new TaskCompletionSource<ExchangeBuyRaw>(TaskCreationOptions.RunContinuationsAsynchronously);
        // allowRefire:false — a buy is a real purchase; fire it exactly once.
        Enqueue(new PendingRpc(KindBuy, BuyGlobal, BuildBuyChunk(itemId, quantity, price), allowRefire: false)
        {
            OnResult = s => tcs.TrySetResult(ParseBuy(s)),
            OnTimeout = () => tcs.TrySetResult(new ExchangeBuyRaw(false, null, true)),
        });
        return tcs.Task;
    }

    private void Enqueue(PendingRpc pending) => _toRegister.Enqueue(pending);

    /// <summary>Drained from the Host service tick (main thread): resolve the bridge, fire newly-registered
    /// requests, then service in-flight ones (read the reply global, re-fire reads for readiness, or time out).</summary>
    internal void DrainPendingDispatches()
    {
        TryResolveBridgeIfDue();
        if (!_bridgeResolved) return;
        var now = DateTime.UtcNow;
        RegisterQueued(now);
        ServiceActive(now);
    }

    // Fire each newly-registered request once; a new request supersedes any in-flight one of the same kind.
    // NOTE: single-flight per kind by design — a second same-kind request cancels (times out) the first.
    // The auto-buyer consumer awaits sequentially, so this is not hit in practice; the public contract just
    // serializes concurrent same-kind calls rather than racing them.
    private void RegisterQueued(DateTime now)
    {
        while (_toRegister.TryDequeue(out var pending))
        {
            _active.RemoveAll(a => a.Kind == pending.Kind && Supersede(a));
            InvokeChunk(ClearGlobalChunk(pending.Global));
            FireAndMark(pending, now);
            _active.Add(pending);
            Diag($"dispatch {pending.Kind}");
        }
    }

    // Read each in-flight request's reply global; complete on a result, re-fire reads for readiness, time out.
    private void ServiceActive(DateTime now)
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var p = _active[i];
            var reply = ReadLuaGlobalString(p.Global);
            if (reply is not null) { _active.RemoveAt(i); Diag($"{p.Kind} resolved"); SafeComplete(() => p.OnResult(reply)); continue; }
            if (now - p.Started >= RequestTimeout) { _active.RemoveAt(i); Diag($"{p.Kind} timeout"); SafeComplete(p.OnTimeout); continue; }
            if (p.AllowRefire && now - p.LastFired >= RefireInterval) FireAndMark(p, now);
        }
    }

    private void FireAndMark(PendingRpc p, DateTime now)
    {
        InvokeChunk(p.Chunk);
        if (p.Started == default) p.Started = now;
        p.LastFired = now;
    }

    private static bool Supersede(PendingRpc old) { old.OnTimeout(); return true; }

    private void SafeComplete(Action complete)
    {
        try { complete(); }
        catch (Exception ex) { _log.Warning($"[Stellar][Exchange] completion threw: {ex.Message}"); }
    }

    private const string KindCare = "care";
    private const string KindListings = "listings";
    private const string KindNotice = "notice";
    private const string KindBuy = "buy";
    private const string KindCatalog = "catalog";

    // One in-flight exchange RPC: which kind, the reply global, the chunk to (re)fire, and completion hooks.
    private sealed class PendingRpc
    {
        public PendingRpc(string kind, string global, string chunk, bool allowRefire)
        {
            Kind = kind;
            Global = global;
            Chunk = chunk;
            AllowRefire = allowRefire;
        }

        public string Kind { get; }
        public string Global { get; }
        public string Chunk { get; }
        public bool AllowRefire { get; }
        public DateTime Started { get; set; }
        public DateTime LastFired { get; set; }
        public Action<string> OnResult { get; init; } = static _ => { };
        public Action OnTimeout { get; init; } = static () => { };
    }
}
