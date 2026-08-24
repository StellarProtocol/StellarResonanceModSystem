using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Exposes <see cref="IDeepSlumber"/>: reads passthrough to <see cref="IDeepSlumberProbe"/>;
/// <see cref="ApplySetupAsync"/> plans the live→target diff (<see cref="DeepSlumberReconciler"/>) and
/// runs it through <see cref="IDeepSlumberWriteProbe"/>, aggregating the per-op codes.</summary>
internal sealed class DeepSlumberService : IDeepSlumber
{
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

        var failed = 0;
        var ok = 0;
        foreach (var op in ops)
        {
            if (ct.IsCancellationRequested) break;
            var code = await Dispatch(op, ct).ConfigureAwait(false);
            if (code == 0) ok++; else failed++;
        }

        if (failed == 0) return DeepSlumberApplyResult.Success;
        return ok > 0 ? DeepSlumberApplyResult.PartialFailure : DeepSlumberApplyResult.Refused;
    }

    private Task<int> Dispatch(DeepSlumberOp op, CancellationToken ct) => op.Kind switch
    {
        DeepSlumberOpKind.EnableLine => _write.EnableLineAsync(op.Key, ct),
        DeepSlumberOpKind.SocketFactor => _write.SocketFactorAsync(op.Key, op.ItemId, ct),
        _ => _write.UnsocketFactorAsync(op.Key, op.CurrentItemId, ct),
    };
}
