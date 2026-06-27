using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Holds a dynamic update-rate request made via <see cref="IFramework.RequestUpdateRate"/>.
/// Dispose to release the request. Idempotent — safe to dispose more than once.
/// </summary>
public interface IUpdateRateScope : IDisposable
{
    /// <summary>True if the request is active (the user granted this plugin rate-control permission);
    /// false for the inert no-op scope returned when permission is off.</summary>
    bool IsActive { get; }
}
