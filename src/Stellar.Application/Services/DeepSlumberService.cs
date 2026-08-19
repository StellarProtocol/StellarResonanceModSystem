using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Wraps <see cref="IDeepSlumberProbe"/> to expose <see cref="IDeepSlumber"/>. Stateless
/// passthrough — every read is live (capture-is-default-on doctrine; no caching layer to go stale).</summary>
internal sealed class DeepSlumberService : IDeepSlumber
{
    private readonly IDeepSlumberProbe _probe;

    public DeepSlumberService(IDeepSlumberProbe probe) => _probe = probe;

    public bool IsAvailable => _probe.IsResolved;

    public DeepSlumberState? GetState() => _probe.Read();
}
