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
/// <para>The ops run in Kind-<b>phases</b> (enable → unsocket → socket) with a barrier between them;
/// within a phase they fire concurrently and the write probe bounds real in-flight parallelism, so a
/// many-factor loadout switch overlaps its server round-trips instead of paying one serially per op.
/// The barrier preserves the two load-bearing invariants across phases — a line is enabled before its
/// factors move, and every scarce single-copy factor is unsocketed (returned to the bag) before any
/// socket needs it. A dropped request (no server reply → a <i>transient</i>
/// <see cref="DeepSlumberWriteCode"/>) is retried; a deterministic game refusal is not.</para></summary>
internal sealed class DeepSlumberService : IDeepSlumber
{
    // Retry only the transient (did-not-land) codes, never a positive game refusal — see
    // DeepSlumberWriteCode. 2 extra attempts with a short backoff covers a server that drops a request
    // fired too close behind another without turning a real refusal into a retry storm.
    private const int MaxRetries = 2;
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromMilliseconds(250);

    // Op-Kind order = the reconciler's flat emit order; one phase per Kind, barrier between phases.
    private static readonly DeepSlumberOpKind[] Phases =
        { DeepSlumberOpKind.EnableLine, DeepSlumberOpKind.UnsocketFactor, DeepSlumberOpKind.SocketFactor };

    private readonly IDeepSlumberProbe _probe;
    private readonly IDeepSlumberWriteProbe _write;

    public DeepSlumberService(IDeepSlumberProbe probe, IDeepSlumberWriteProbe write)
    {
        _probe = probe;
        _write = write;
    }

    public bool IsAvailable => _probe.IsResolved;

    public DeepSlumberState? GetState() => _probe.Read();

    public async Task<DeepSlumberApplyResult> ApplySetupAsync(DeepSlumberSetup target, CancellationToken ct = default)
    {
        if (!_write.IsResolved) return DeepSlumberApplyResult.Unavailable;
        var current = _probe.Read();
        if (current is null) return DeepSlumberApplyResult.Unavailable;

        var ops = DeepSlumberReconciler.Plan(current, target);
        if (ops.Count == 0) return DeepSlumberApplyResult.AlreadyMatched;

        var ok = 0;
        var failed = 0;
        var cancelled = false;

        foreach (var phase in Phases)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            var phaseOps = OpsOfKind(ops, phase);
            if (phaseOps.Count == 0) continue;

            var tasks = new Task<int>[phaseOps.Count];
            for (var i = 0; i < phaseOps.Count; i++) tasks[i] = DispatchWithRetry(phaseOps[i], ct);
            var codes = await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var code in codes)
            {
                if (code == DeepSlumberWriteCode.Ok) ok++; else failed++;
            }
            if (ct.IsCancellationRequested) { cancelled = true; break; }
        }

        // cancelled short-circuits: any cancelled op returns a non-Ok code and inflates `failed`, but a
        // partial user-cancel is Cancelled (nothing applied) / PartialFailure (some applied), not Refused.
        if (cancelled) return ok > 0 ? DeepSlumberApplyResult.PartialFailure : DeepSlumberApplyResult.Cancelled;
        if (failed == 0) return DeepSlumberApplyResult.Success;
        return ok > 0 ? DeepSlumberApplyResult.PartialFailure : DeepSlumberApplyResult.Refused;
    }

    private static List<DeepSlumberOp> OpsOfKind(IReadOnlyList<DeepSlumberOp> ops, DeepSlumberOpKind kind)
    {
        var result = new List<DeepSlumberOp>();
        foreach (var op in ops) if (op.Kind == kind) result.Add(op);
        return result;
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
        DeepSlumberOpKind.SocketFactor => _write.SocketFactorAsync(op.Key, op.ItemId, ct),
        _ => _write.UnsocketFactorAsync(op.Key, op.CurrentItemId, ct),
    };
}
