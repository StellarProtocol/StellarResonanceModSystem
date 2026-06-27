using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>Shared no-op <see cref="IUpdateRateScope"/> returned when a plugin lacks rate-control
/// permission. Allocation-free (single instance); dispose does nothing.</summary>
internal sealed class InertUpdateRateScope : IUpdateRateScope
{
    public static readonly InertUpdateRateScope Instance = new();
    private InertUpdateRateScope() { }
    public bool IsActive => false;
    public void Dispose() { }
}
