using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Wraps <see cref="IDeepSlumberProbe"/> to expose <see cref="IDeepSlumber"/>. Passthrough —
/// this service holds no state of its own; every read serves the probe's last on-demand parse
/// (capture-is-default-on doctrine). The probe clears its parsed state on logout
/// (<c>PandaLoadoutProbe.ClearSession</c>), so a relog never leaks the previous character's state
/// through this passthrough.</summary>
internal sealed class DeepSlumberService : IDeepSlumber
{
    private readonly IDeepSlumberProbe _probe;

    public DeepSlumberService(IDeepSlumberProbe probe) => _probe = probe;

    public bool IsAvailable => _probe.IsResolved;

    public DeepSlumberState? GetState() => _probe.Read();
}
