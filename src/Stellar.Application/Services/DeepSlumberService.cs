using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Exposes <see cref="IDeepSlumber"/>: reads passthrough to <see cref="IDeepSlumberProbe"/>;
/// <see cref="ApplySetupAsync"/> plans the live→target diff (<see cref="DeepSlumberReconciler"/>) and
/// runs it through <see cref="IDeepSlumberWriteProbe"/>, aggregating the per-op codes.
///
/// <para>The ops run in Kind-<b>phases</b> (enable → reset → unsocket → activate → socket) with a
/// barrier between them; within a phase they fire concurrently and the write probe bounds real in-flight
/// parallelism, so a many-op loadout switch overlaps its server round-trips instead of paying one
/// serially per op. The barrier preserves the load-bearing invariants across phases — a line is enabled
/// before its nodes move, a differing tree is reset before it is rebuilt, every scarce single-copy factor
/// is unsocketed (returned to the bag) before any socket needs it, and the target tree is activated
/// before its cursors accept a factor. A dropped request (no server reply → a <i>transient</i>
/// <see cref="DeepSlumberWriteCode"/>) is retried; a deterministic game refusal is not.</para></summary>
internal sealed class DeepSlumberService : IDeepSlumber
{
    // Retry only the transient (did-not-land) codes, never a positive game refusal — see
    // DeepSlumberWriteCode. 2 extra attempts with a short backoff covers a server that drops a request
    // fired too close behind another without turning a real refusal into a retry storm.
    private const int MaxRetries = 2;
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromMilliseconds(250);

    // Op-Kind order = the reconciler's flat emit order; one phase per Kind, barrier between phases.
    // Enable a line before touching its nodes; reset a differing area before rebuilding it; free scarce
    // factors (unsocket) before any socket; activate the target tree before its cursors accept a factor.
    private static readonly DeepSlumberOpKind[] Phases =
    {
        DeepSlumberOpKind.EnableLine, DeepSlumberOpKind.ResetNodes, DeepSlumberOpKind.UnsocketFactor,
        DeepSlumberOpKind.ActivateNode, DeepSlumberOpKind.SocketFactor,
    };

    private readonly IDeepSlumberProbe _probe;
    private readonly IDeepSlumberWriteProbe _write;
    private readonly IFactorBagProbe _bag;

    // bag defaults to the null-object probe (empty counts ⇒ the reconciler's conservative foreign-free);
    // production wiring passes the real inventory probe so bag-aware free is active.
    public DeepSlumberService(IDeepSlumberProbe probe, IDeepSlumberWriteProbe write, IFactorBagProbe? bag = null)
    {
        _probe = probe;
        _write = write;
        _bag = bag ?? EmptyFactorBagProbe.Instance;
    }

    public bool IsAvailable => _probe.IsResolved;

    public DeepSlumberState? GetState() => _probe.Read();

    public async Task<DeepSlumberApplyResult> ApplySetupAsync(DeepSlumberSetup target, CancellationToken ct = default)
    {
        if (!_write.IsResolved) return DeepSlumberApplyResult.Unavailable;
        var current = _probe.Read();
        if (current is null) return DeepSlumberApplyResult.Unavailable;

        // Inventory check FIRST (owner 2026-09-22): the reconciler frees a factor from an inactive loadout
        // only when the target can't otherwise obtain it — never when the target already has it or a spare
        // is in the bag.
        var ops = DeepSlumberReconciler.Plan(current, target, _bag.ReadFactorBagCounts(TargetFactorIds(target)));
        if (ops.Count == 0) return DeepSlumberApplyResult.AlreadyMatched;

        // Reactive backstop pool: current-season foreign copies of the target's factors the predictive pass
        // left in place — freed ONLY if the game then refuses a socket 7561 (a copy in an inactive tree
        // counts toward the per-type class limit, which a bag spare cannot clear).
        var freePool = BuildForeignFreePool(current, target, ops);

        var ok = 0;
        var failed = 0;
        var cancelled = false;

        foreach (var phase in Phases)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            var phaseOps = OpsOfKind(ops, phase);
            if (phaseOps.Count == 0) continue;

            // ActivateNode converges on node prerequisites (5126); SocketFactor frees a foreign copy on a
            // 7561 refusal and retries; every other phase fires its ops concurrently once.
            var (phaseOk, phaseFailed) = phase switch
            {
                DeepSlumberOpKind.ActivateNode => await ActivatePhaseAsync(phaseOps, ct).ConfigureAwait(false),
                DeepSlumberOpKind.SocketFactor => await SocketPhaseAsync(phaseOps, freePool, ct).ConfigureAwait(false),
                _ => await RunPhaseAsync(phaseOps, ct).ConfigureAwait(false),
            };
            ok += phaseOk;
            failed += phaseFailed;
            if (ct.IsCancellationRequested) { cancelled = true; break; }
        }

        // cancelled short-circuits: any cancelled op returns a non-Ok code and inflates `failed`, but a
        // partial user-cancel is Cancelled (nothing applied) / PartialFailure (some applied), not Refused.
        if (cancelled) return ok > 0 ? DeepSlumberApplyResult.PartialFailure : DeepSlumberApplyResult.Cancelled;
        if (failed == 0) return DeepSlumberApplyResult.Success;
        return ok > 0 ? DeepSlumberApplyResult.PartialFailure : DeepSlumberApplyResult.Refused;
    }

    // The distinct factor itemIds the target socket-set could need from the bag (itemId 0 = empty).
    private static IReadOnlyCollection<int> TargetFactorIds(DeepSlumberSetup target)
    {
        var ids = new HashSet<int>();
        foreach (var area in target.Areas)
            foreach (var f in area.Factors)
                if (f.Length >= 2 && f[1] != 0) ids.Add(f[1]);
        return ids;
    }

    private static List<DeepSlumberOp> OpsOfKind(IReadOnlyList<DeepSlumberOp> ops, DeepSlumberOpKind kind)
    {
        var result = new List<DeepSlumberOp>();
        foreach (var op in ops) if (op.Kind == kind) result.Add(op);
        return result;
    }

    // Fire a phase's ops concurrently, once — the barrier model for every phase except ActivateNode.
    private async Task<(int Ok, int Failed)> RunPhaseAsync(List<DeepSlumberOp> phaseOps, CancellationToken ct)
    {
        var tasks = new Task<int>[phaseOps.Count];
        for (var i = 0; i < phaseOps.Count; i++) tasks[i] = DispatchWithRetry(phaseOps[i], ct);
        var codes = await Task.WhenAll(tasks).ConfigureAwait(false);
        int ok = 0, failed = 0;
        foreach (var code in codes) { if (code == DeepSlumberWriteCode.Ok) ok++; else failed++; }
        return (ok, failed);
    }

    // Anchors form a dependency TREE: activating a node whose parent isn't active yet fails 5126
    // (ErrTalentPreTalentNodeNotActivated) — a node's factor socket is irrelevant to activation (owner
    // 2026-09-22). The reconciler's target anchor set is a fully-captured tree (closed under prerequisite),
    // so retry ONLY the 5126s in passes: each pass fires the still-pending nodes concurrently, keeps the
    // ones that landed, and requeues the 5126s — until all activate or a pass makes NO progress (a
    // genuinely un-activatable remainder is then failed). This self-orders activation into
    // parent-before-child WITHOUT the dependency graph, so a class round-trip rebuilds the whole tree
    // instead of half-completing and leaving it locked. Bounded: each pass either lands ≥1 node or stops.
    private async Task<(int Ok, int Failed)> ActivatePhaseAsync(List<DeepSlumberOp> phaseOps, CancellationToken ct)
    {
        var pending = phaseOps;
        int ok = 0, failed = 0;
        while (pending.Count > 0 && !ct.IsCancellationRequested)
        {
            var tasks = new Task<int>[pending.Count];
            for (var i = 0; i < pending.Count; i++) tasks[i] = DispatchWithRetry(pending[i], ct);
            var codes = await Task.WhenAll(tasks).ConfigureAwait(false);

            var blocked = new List<DeepSlumberOp>();
            var landed = 0;
            for (var i = 0; i < pending.Count; i++)
            {
                if (codes[i] == DeepSlumberWriteCode.Ok) { ok++; landed++; }
                else if (codes[i] == DeepSlumberWriteCode.PreTalentNodeNotActivated) blocked.Add(pending[i]);
                else failed++;                                   // terminal, non-ordering failure
            }
            if (landed == 0) { failed += blocked.Count; break; } // no progress → remainder can't activate
            pending = blocked;
        }
        return (ok, failed);
    }

    // Socket the target factors — the REACTIVE-free backstop AFTER the predictive inventory-checked pass.
    // A socket refused 7561 (ItemClassNumExceeded: a copy in an inactive tree counts toward the per-type
    // limit — a bag spare can't clear it) is retried after freeing one foreign copy of that factor from the
    // pool. Passes repeat until every socket lands or a pass frees nothing more (pool exhausted / a
    // non-7561 refusal). Freeing strictly drains the finite pool, so it always terminates.
    private async Task<(int Ok, int Failed)> SocketPhaseAsync(
        List<DeepSlumberOp> phaseOps, Dictionary<int, Queue<int>> freePool, CancellationToken ct)
    {
        var pending = phaseOps;
        int ok = 0, failed = 0;
        while (pending.Count > 0 && !ct.IsCancellationRequested)
        {
            var tasks = new Task<int>[pending.Count];
            for (var i = 0; i < pending.Count; i++) tasks[i] = DispatchWithRetry(pending[i], ct);
            var codes = await Task.WhenAll(tasks).ConfigureAwait(false);

            var retry = new List<DeepSlumberOp>();
            for (var i = 0; i < pending.Count; i++)
            {
                if (codes[i] == DeepSlumberWriteCode.Ok) { ok++; continue; }
                if (codes[i] == DeepSlumberWriteCode.ItemClassNumExceeded
                    && freePool.TryGetValue(pending[i].ItemId, out var q) && q.Count > 0
                    && await FreeForRetryAsync(q.Dequeue(), pending[i].ItemId, ct).ConfigureAwait(false))
                    retry.Add(pending[i]);                       // freed a foreign copy → retry this socket
                else
                    failed++;                                    // not 7561, no copy left, or the free failed
            }
            if (retry.Count == 0) break;                         // nothing freed this pass → done
            pending = retry;
        }
        return (ok, failed);
    }

    private async Task<bool> FreeForRetryAsync(int node, int itemId, CancellationToken ct)
        => await DispatchWithRetry(DeepSlumberOp.Unsocket(node, itemId), ct).ConfigureAwait(false)
           == DeepSlumberWriteCode.Ok;

    // Foreign copies (itemId → node queue) the predictive pass left in place — the reactive-free pool.
    // Excludes any (node,item) the plan already unsocketed so a reactive free never double-frees a node.
    private static Dictionary<int, Queue<int>> BuildForeignFreePool(
        DeepSlumberState current, DeepSlumberSetup target, IReadOnlyList<DeepSlumberOp> ops)
    {
        var planned = new HashSet<(int Node, int Item)>();
        foreach (var op in ops)
            if (op.Kind == DeepSlumberOpKind.UnsocketFactor) planned.Add((op.Key, op.CurrentItemId));

        var pool = new Dictionary<int, Queue<int>>();
        foreach (var (item, node) in DeepSlumberReconciler.ForeignSharedFactorCandidates(current, target))
        {
            if (planned.Contains((node, item))) continue;
            if (!pool.TryGetValue(item, out var q)) pool[item] = q = new Queue<int>();
            q.Enqueue(node);
        }
        return pool;
    }

    // Dispatch one op, retrying ONLY a transient (did-not-land) code — never a positive game refusal
    // (retrying a deterministic 7555/7561 fails identically and could misreport a succeeded-but-reply-
    // lost op as a failure). Never throws: a token firing during the backoff ends the retry loop and
    // returns the last code, which the caller's post-phase ct check resolves to Cancelled/PartialFailure.
    private async Task<int> DispatchWithRetry(DeepSlumberOp op, CancellationToken ct)
    {
        var code = await Dispatch(op, ct).ConfigureAwait(false);
        for (var attempt = 0;
             attempt < MaxRetries && DeepSlumberWriteCode.IsTransient(code) && !ct.IsCancellationRequested;
             attempt++)
        {
            try { await Task.Delay(RetryBackoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            code = await Dispatch(op, ct).ConfigureAwait(false);
        }
        return code;
    }

    private Task<int> Dispatch(DeepSlumberOp op, CancellationToken ct) => op.Kind switch
    {
        DeepSlumberOpKind.EnableLine => _write.EnableLineAsync(op.Key, ct),
        DeepSlumberOpKind.ResetNodes => _write.ResetNodesAsync(op.Key, ct),
        DeepSlumberOpKind.ActivateNode => _write.ActivateNodeAsync(op.Key, ct),
        DeepSlumberOpKind.SocketFactor => _write.SocketFactorAsync(op.Key, op.ItemId, ct),
        _ => _write.UnsocketFactorAsync(op.Key, op.CurrentItemId, ct),
    };
}
